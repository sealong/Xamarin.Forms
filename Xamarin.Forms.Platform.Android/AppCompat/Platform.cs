using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Android.Content;
using Android.Views;
using Android.Views.Animations;
using ARelativeLayout = Android.Widget.RelativeLayout;

namespace Xamarin.Forms.Platform.Android.AppCompat
{
	internal class Platform : BindableObject, IPlatform, IPlatformLayout, INavigation, IDisposable
	{
		readonly Context _context;
		readonly PlatformRenderer _renderer;
		bool _disposed;
		bool _navAnimationInProgress;
		NavigationModel _navModel = new NavigationModel();

		public Platform(Context context)
		{
			_context = context;

			_renderer = new PlatformRenderer(context, this);

			FormsAppCompatActivity.BackPressed += HandleBackPressed;
		}

		internal bool NavAnimationInProgress
		{
			get { return _navAnimationInProgress; }
			set
			{
				if (_navAnimationInProgress == value)
					return;
				_navAnimationInProgress = value;
				if (value)
					MessagingCenter.Send(this, CloseContextActionsSignalName);
			}
		}

		Page Page { get; set; }

		public void Dispose()
		{
			if (_disposed)
				return;
			_disposed = true;

			SetPage(null);

			FormsAppCompatActivity.BackPressed -= HandleBackPressed;
		}

		void INavigation.InsertPageBefore(Page page, Page before)
		{
			throw new InvalidOperationException("InsertPageBefore is not supported globally on Android, please use a NavigationPage.");
		}

		IReadOnlyList<Page> INavigation.ModalStack => _navModel.Modals.ToList();

		IReadOnlyList<Page> INavigation.NavigationStack => new List<Page>();

		Task<Page> INavigation.PopAsync()
		{
			return ((INavigation)this).PopAsync(true);
		}

		Task<Page> INavigation.PopAsync(bool animated)
		{
			throw new InvalidOperationException("PopAsync is not supported globally on Android, please use a NavigationPage.");
		}

		Task<Page> INavigation.PopModalAsync()
		{
			return ((INavigation)this).PopModalAsync(true);
		}

		Task<Page> INavigation.PopModalAsync(bool animated)
		{
			Page modal = _navModel.PopModal();
			modal.SendDisappearing();
			var source = new TaskCompletionSource<Page>();

			IVisualElementRenderer modalRenderer = Android.Platform.GetRenderer(modal);
			if (modalRenderer != null)
			{
				var modalContainer = modalRenderer.ViewGroup.Parent as ModalContainer;
				if (animated)
				{
					modalContainer.Animate().TranslationY(_renderer.Height).SetInterpolator(new AccelerateInterpolator(1)).SetDuration(300).SetListener(new GenericAnimatorListener
					{
						OnEnd = a =>
						{
							modalContainer.RemoveFromParent();
							modalContainer.Dispose();
							source.TrySetResult(modal);
							_navModel.CurrentPage?.SendAppearing();
							modalContainer = null;
						}
					});
				}
				else
				{
					modalContainer.RemoveFromParent();
					modalContainer.Dispose();
					source.TrySetResult(modal);
					_navModel.CurrentPage?.SendAppearing();
				}
			}

			return source.Task;
		}

		Task INavigation.PopToRootAsync()
		{
			return ((INavigation)this).PopToRootAsync(true);
		}

		Task INavigation.PopToRootAsync(bool animated)
		{
			throw new InvalidOperationException("PopToRootAsync is not supported globally on Android, please use a NavigationPage.");
		}

		Task INavigation.PushAsync(Page root)
		{
			return ((INavigation)this).PushAsync(root, true);
		}

		Task INavigation.PushAsync(Page root, bool animated)
		{
			throw new InvalidOperationException("PushAsync is not supported globally on Android, please use a NavigationPage.");
		}

		Task INavigation.PushModalAsync(Page modal)
		{
			return ((INavigation)this).PushModalAsync(modal, true);
		}

		async Task INavigation.PushModalAsync(Page modal, bool animated)
		{
			_navModel.CurrentPage?.SendDisappearing();

			_navModel.PushModal(modal);

			modal.Platform = this;

			Task presentModal = PresentModal(modal, animated);

			await presentModal;

			// Verify that the modal is still on the stack
			if (_navModel.CurrentPage == modal)
				modal.SendAppearing();
		}

		void INavigation.RemovePage(Page page)
		{
			throw new InvalidOperationException("RemovePage is not supported globally on Android, please use a NavigationPage.");
		}

		SizeRequest IPlatform.GetNativeSize(VisualElement view, double widthConstraint, double heightConstraint)
		{
			Performance.Start();

			// FIXME: potential crash
			IVisualElementRenderer viewRenderer = Android.Platform.GetRenderer(view);

			// negative numbers have special meanings to android they don't to us
			widthConstraint = widthConstraint <= -1 ? double.PositiveInfinity : _context.ToPixels(widthConstraint);
			heightConstraint = heightConstraint <= -1 ? double.PositiveInfinity : _context.ToPixels(heightConstraint);

			int width = !double.IsPositiveInfinity(widthConstraint)
							? MeasureSpecFactory.MakeMeasureSpec((int)widthConstraint, MeasureSpecMode.AtMost)
							: MeasureSpecFactory.MakeMeasureSpec(0, MeasureSpecMode.Unspecified);

			int height = !double.IsPositiveInfinity(heightConstraint)
							 ? MeasureSpecFactory.MakeMeasureSpec((int)heightConstraint, MeasureSpecMode.AtMost)
							 : MeasureSpecFactory.MakeMeasureSpec(0, MeasureSpecMode.Unspecified);

			SizeRequest rawResult = viewRenderer.GetDesiredSize(width, height);
			if (rawResult.Minimum == Size.Zero)
				rawResult.Minimum = rawResult.Request;
			var result = new SizeRequest(new Size(_context.FromPixels(rawResult.Request.Width), _context.FromPixels(rawResult.Request.Height)),
				new Size(_context.FromPixels(rawResult.Minimum.Width), _context.FromPixels(rawResult.Minimum.Height)));

			Performance.Stop();
			return result;
		}

		void IPlatformLayout.OnLayout(bool changed, int l, int t, int r, int b)
		{
			if (changed)
				LayoutRootPage(Page, r - l, b - t);

			Android.Platform.GetRenderer(Page).UpdateLayout();

			for (var i = 0; i < _renderer.ChildCount; i++)
			{
				global::Android.Views.View child = _renderer.GetChildAt(i);
				if (child is ModalContainer)
				{
					child.Measure(MeasureSpecFactory.MakeMeasureSpec(r - l, MeasureSpecMode.Exactly), MeasureSpecFactory.MakeMeasureSpec(t - b, MeasureSpecMode.Exactly));
					child.Layout(l, t, r, b);
				}
			}
		}

		protected override void OnBindingContextChanged()
		{
			SetInheritedBindingContext(Page, BindingContext);

			base.OnBindingContextChanged();
		}

		internal void SetPage(Page newRoot)
		{
			var layout = false;
			if (Page != null)
			{
				_renderer.RemoveAllViews();

				foreach (IVisualElementRenderer rootRenderer in _navModel.Roots.Select(Android.Platform.GetRenderer))
					rootRenderer.Dispose();
				_navModel = new NavigationModel();

				layout = true;
			}

			if (newRoot == null)
				return;

			_navModel.Push(newRoot, null);

			Page = newRoot;
			Page.Platform = this;
			AddChild(Page, layout);

			((Application)Page.RealParent).NavigationProxy.Inner = this;
		}

		void AddChild(Page page, bool layout = false)
		{
			if (Android.Platform.GetRenderer(page) != null)
				return;

			Android.Platform.SetPageContext(page, _context);
			IVisualElementRenderer renderView = Android.Platform.CreateRenderer(page);
			Android.Platform.SetRenderer(page, renderView);

			if (layout)
				LayoutRootPage(page, _renderer.Width, _renderer.Height);

			_renderer.AddView(renderView.ViewGroup);
		}

		bool HandleBackPressed(object sender, EventArgs e)
		{
			if (NavAnimationInProgress)
				return true;

			Page root = _navModel.Roots.Last();
			bool handled = root.SendBackButtonPressed();

			return handled;
		}

		void LayoutRootPage(Page page, int width, int height)
		{
			var activity = (FormsAppCompatActivity)_context;
			int statusBarHeight = Forms.IsLollipopOrNewer ? activity.GetStatusBarHeight() : 0;

			if (page is MasterDetailPage)
				page.Layout(new Rectangle(0, 0, _context.FromPixels(width), _context.FromPixels(height)));
			else
			{
				page.Layout(new Rectangle(0, _context.FromPixels(statusBarHeight), _context.FromPixels(width), _context.FromPixels(height - statusBarHeight)));
			}
		}

		Task PresentModal(Page modal, bool animated)
		{
			var modalContainer = new ModalContainer(_context, modal);

			_renderer.AddView(modalContainer);

			var source = new TaskCompletionSource<bool>();
			NavAnimationInProgress = true;
			if (animated)
			{
				modalContainer.TranslationY = _renderer.Height;
				modalContainer.Animate().TranslationY(0).SetInterpolator(new DecelerateInterpolator(1)).SetDuration(300).SetListener(new GenericAnimatorListener
				{
					OnEnd = a =>
					{
						source.TrySetResult(false);
						NavAnimationInProgress = false;
						modalContainer = null;
					},
					OnCancel = a =>
					{
						source.TrySetResult(true);
						NavAnimationInProgress = false;
						modalContainer = null;
					}
				});
			}
			else
			{
				NavAnimationInProgress = false;
				source.TrySetResult(true);
			}

			return source.Task;
		}

		sealed class ModalContainer : ViewGroup
		{
			global::Android.Views.View _backgroundView;
			bool _disposed;
			Page _modal;
			IVisualElementRenderer _renderer;

			public ModalContainer(Context context, Page modal) : base(context)
			{
				_modal = modal;

				_backgroundView = new global::Android.Views.View(context);
				_backgroundView.SetWindowBackground();
				AddView(_backgroundView);

				Android.Platform.SetPageContext(modal, context);
				_renderer = Android.Platform.CreateRenderer(modal);
				Android.Platform.SetRenderer(modal, _renderer);

				AddView(_renderer.ViewGroup);
			}

			protected override void Dispose(bool disposing)
			{
				if (disposing && !_disposed)
				{
					_disposed = true;
					RemoveAllViews();
					if (_renderer != null)
					{
						_renderer.Dispose();
						_renderer = null;
						_modal.ClearValue(Android.Platform.RendererProperty);
						_modal = null;
					}

					if (_backgroundView != null)
					{
						_backgroundView.Dispose();
						_backgroundView = null;
					}
				}

				base.Dispose(disposing);
			}

			protected override void OnLayout(bool changed, int l, int t, int r, int b)
			{
				var activity = (FormsAppCompatActivity)Context;
				int statusBarHeight = Forms.IsLollipopOrNewer ? activity.GetStatusBarHeight() : 0;
				if (changed)
				{
					if (_modal is MasterDetailPage)
						_modal.Layout(new Rectangle(0, 0, activity.FromPixels(r - l), activity.FromPixels(b - t)));
					else
					{
						_modal.Layout(new Rectangle(0, activity.FromPixels(statusBarHeight), activity.FromPixels(r - l), activity.FromPixels(b - t - statusBarHeight)));
					}

					_backgroundView.Layout(0, statusBarHeight, r - l, b - t);
				}

				_renderer.UpdateLayout();
			}
		}

		#region Statics

		public static implicit operator ViewGroup(Platform canvas)
		{
			return canvas._renderer;
		}

		internal const string CloseContextActionsSignalName = "Xamarin.CloseContextActions";

		#endregion
	}
}