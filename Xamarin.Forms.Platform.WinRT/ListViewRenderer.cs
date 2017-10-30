﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Automation.Peers;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Data;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using WListView = Windows.UI.Xaml.Controls.ListView;
using WBinding = Windows.UI.Xaml.Data.Binding;
using WApp = Windows.UI.Xaml.Application;
using Xamarin.Forms.Internals;
using Xamarin.Forms.PlatformConfiguration.WindowsSpecific;
using Specifics = Xamarin.Forms.PlatformConfiguration.WindowsSpecific.ListView;

#if WINDOWS_UWP

namespace Xamarin.Forms.Platform.UWP
#else

namespace Xamarin.Forms.Platform.WinRT
#endif
{
	public class ListViewRenderer : ViewRenderer<ListView, FrameworkElement>
	{
		ITemplatedItemsView<Cell> TemplatedItemsView => Element;
		bool _itemWasClicked;
		bool _subscribedToItemClick;
		bool _subscribedToTapped;


#if !WINDOWS_UWP
		public static readonly DependencyProperty HighlightWhenSelectedProperty = DependencyProperty.RegisterAttached("HighlightWhenSelected", typeof(bool), typeof(ListViewRenderer),
			new PropertyMetadata(false));

		public static bool GetHighlightWhenSelected(DependencyObject dependencyObject)
		{
			return (bool)dependencyObject.GetValue(HighlightWhenSelectedProperty);
		}

		public static void SetHighlightWhenSelected(DependencyObject dependencyObject, bool value)
		{
			dependencyObject.SetValue(HighlightWhenSelectedProperty, value);
		}
#endif

		protected WListView List { get; private set; }

		protected override void OnElementChanged(ElementChangedEventArgs<ListView> e)
		{
			base.OnElementChanged(e);

			if (e.OldElement != null)
			{
				e.OldElement.ItemSelected -= OnElementItemSelected;
				e.OldElement.ScrollToRequested -= OnElementScrollToRequested;
			}

			if (e.NewElement != null)
			{
				e.NewElement.ItemSelected += OnElementItemSelected;
				e.NewElement.ScrollToRequested += OnElementScrollToRequested;

				if (List == null)
				{
					List = new WListView
					{
						IsSynchronizedWithCurrentItem = false,
						ItemTemplate = (Windows.UI.Xaml.DataTemplate)WApp.Current.Resources["CellTemplate"],
						HeaderTemplate = (Windows.UI.Xaml.DataTemplate)WApp.Current.Resources["View"],
						FooterTemplate = (Windows.UI.Xaml.DataTemplate)WApp.Current.Resources["View"],
						ItemContainerStyle = (Windows.UI.Xaml.Style)WApp.Current.Resources["FormsListViewItem"],
						GroupStyleSelector = (GroupStyleSelector)WApp.Current.Resources["ListViewGroupSelector"]
					};

					List.SelectionChanged += OnControlSelectionChanged;

					List.SetBinding(ItemsControl.ItemsSourceProperty, "");
				}

				// WinRT throws an exception if you set ItemsSource directly to a CVS, so bind it.
				List.DataContext = new CollectionViewSource { Source = Element.ItemsSource, IsSourceGrouped = Element.IsGroupingEnabled };

#if !WINDOWS_UWP
				var selected = Element.SelectedItem;
				if (selected != null)
					OnElementItemSelected(null, new SelectedItemChangedEventArgs(selected));
#endif

				UpdateGrouping();
				UpdateHeader();
				UpdateFooter();
				UpdateSelectionMode();
				ClearSizeEstimate();
			}
		}

		protected override void OnElementPropertyChanged(object sender, PropertyChangedEventArgs e)
		{
			base.OnElementPropertyChanged(sender, e);

			if (e.PropertyName == ListView.IsGroupingEnabledProperty.PropertyName)
			{
				UpdateGrouping();
			}
			else if (e.PropertyName == ListView.HeaderProperty.PropertyName)
			{
				UpdateHeader();
			}
			else if (e.PropertyName == ListView.FooterProperty.PropertyName)
			{
				UpdateFooter();
			}
			else if (e.PropertyName == ListView.RowHeightProperty.PropertyName)
			{
				ClearSizeEstimate();
			}
			else if (e.PropertyName == ListView.HasUnevenRowsProperty.PropertyName)
			{
				ClearSizeEstimate();
			}
			else if (e.PropertyName == ListView.ItemTemplateProperty.PropertyName)
			{
				ClearSizeEstimate();
			}
			else if (e.PropertyName == ListView.ItemsSourceProperty.PropertyName)
			{
				ClearSizeEstimate();
				((CollectionViewSource)List.DataContext).Source = Element.ItemsSource;
			}
			else if (e.PropertyName == Specifics.SelectionModeProperty.PropertyName)
			{
				UpdateSelectionMode();
			}
		}

		protected override void Dispose(bool disposing)
		{
			if (List != null)
			{
				if (_subscribedToTapped)
				{
					_subscribedToTapped = false;
					List.Tapped -= ListOnTapped;
				}
				if (_subscribedToItemClick)
				{
					_subscribedToItemClick = false;
					List.ItemClick -= OnListItemClicked;
				}
				List.SelectionChanged -= OnControlSelectionChanged;

				List.DataContext = null;
				List = null;
			}

			if (_zoom != null)
			{
				_zoom.ViewChangeCompleted -= OnViewChangeCompleted;
				_zoom = null;
			}

			base.Dispose(disposing);
		}

		static IEnumerable<T> FindDescendants<T>(DependencyObject dobj) where T : DependencyObject
		{
			int count = VisualTreeHelper.GetChildrenCount(dobj);
			for (var i = 0; i < count; i++)
			{
				DependencyObject element = VisualTreeHelper.GetChild(dobj, i);
				if (element is T)
					yield return (T)element;

				foreach (T descendant in FindDescendants<T>(element))
					yield return descendant;
			}
		}

		sealed class BrushedElement
		{
			public BrushedElement(FrameworkElement element, WBinding brushBinding = null, Brush brush = null)
			{
				Element = element;
				BrushBinding = brushBinding;
				Brush = brush;
			}

			public Brush Brush { get; }

			public WBinding BrushBinding { get; }

			public FrameworkElement Element { get; }

			public bool IsBound
			{
				get { return BrushBinding != null; }
			}
		}

		SemanticZoom _zoom;
		ScrollViewer _scrollViewer;
		ContentControl _headerControl;
		readonly List<BrushedElement> _highlightedElements = new List<BrushedElement>();

		void ClearSizeEstimate()
		{
			Element.ClearValue(CellControl.MeasuredEstimateProperty);
		}

		void UpdateFooter()
		{
			List.Footer = Element.FooterElement;
		}

		void UpdateHeader()
		{
			List.Header = Element.HeaderElement;
		}

		void UpdateGrouping()
		{
			bool grouping = Element.IsGroupingEnabled;

			((CollectionViewSource)List.DataContext).IsSourceGrouped = grouping;

			var templatedItems = TemplatedItemsView.TemplatedItems;
			if (grouping && templatedItems.ShortNames != null)
			{
				if (_zoom == null)
				{
					ScrollViewer.SetIsVerticalScrollChainingEnabled(List, false);

					var grid = new GridView { ItemsSource = templatedItems.ShortNames, Style = (Windows.UI.Xaml.Style)WApp.Current.Resources["JumpListGrid"] };

					ScrollViewer.SetIsHorizontalScrollChainingEnabled(grid, false);

					_zoom = new SemanticZoom { IsZoomOutButtonEnabled = false, ZoomedOutView = grid };

					// Since we reuse our ScrollTo, we have to wait until the change completes or ChangeView has odd behavior.
					_zoom.ViewChangeCompleted += OnViewChangeCompleted;

					// Specific order to let SNC unparent the ListView for us
					SetNativeControl(_zoom);
					_zoom.ZoomedInView = List;
				}
				else
				{
					_zoom.CanChangeViews = true;
				}
			}
			else
			{
				if (_zoom != null)
					_zoom.CanChangeViews = false;
				else if (List != Control)
					SetNativeControl(List);
			}
		}

		void UpdateSelectionMode()
		{
			if (Element.OnThisPlatform().GetSelectionMode() == PlatformConfiguration.WindowsSpecific.ListViewSelectionMode.Accessible)
			{
				// Using Tapped will disable the ability to use the Enter key
				List.IsItemClickEnabled = true;
				if (!_subscribedToItemClick)
				{
					_subscribedToItemClick = true;
					List.ItemClick += OnListItemClicked;
				}

				if (_subscribedToTapped)
				{
					_subscribedToTapped = false;
					List.Tapped -= ListOnTapped;
				}
			}
			else
			{
				// In order to support tapping on elements within a list item, we handle 
				// ListView.Tapped (which can be handled by child elements in the list items 
				// and prevented from bubbling up) rather than ListView.ItemClick 
				if (!_subscribedToTapped)
				{
					_subscribedToTapped = true;
					List.Tapped += ListOnTapped;
				}

				List.IsItemClickEnabled = false;
				if (_subscribedToItemClick)
				{
					_subscribedToItemClick = false;
					List.ItemClick -= OnListItemClicked;
				}
			}
		}

		async void OnViewChangeCompleted(object sender, SemanticZoomViewChangedEventArgs e)
		{
			if (e.IsSourceZoomedInView)
				return;

			// HACK: Technically more than one short name could be the same, this will potentially find the wrong one in that case
			var item = (string)e.SourceItem.Item;

			var templatedItems = TemplatedItemsView.TemplatedItems;
			int index = templatedItems.ShortNames.IndexOf(item);
			if (index == -1)
				return;

			var til = templatedItems.GetGroup(index);
			if (til.Count == 0)
				return; // FIXME

			// Delay until after the SemanticZoom change _actually_ finishes, fixes tons of odd issues on Phone w/ virtualization.
			if (Device.Idiom == TargetIdiom.Phone)
				await Task.Delay(1);

			IListProxy listProxy = til.ListProxy;
			ScrollTo(listProxy.ProxiedEnumerable, listProxy[0], ScrollToPosition.Start, true, true);
		}

#pragma warning disable 1998 // considered for removal
		async void ScrollTo(object group, object item, ScrollToPosition toPosition, bool shouldAnimate, bool includeGroup = false, bool previouslyFailed = false)
#pragma warning restore 1998
		{
			ScrollViewer viewer = GetScrollViewer();
			if (viewer == null)
			{
				RoutedEventHandler loadedHandler = null;
				loadedHandler = async (o, e) =>
				{
					List.Loaded -= loadedHandler;

					// Here we try to avoid an exception, see explanation at bottom
					await Dispatcher.RunIdleAsync(args => { ScrollTo(group, item, toPosition, shouldAnimate, includeGroup); });
				};
				List.Loaded += loadedHandler;
				return;
			}
			var templatedItems = TemplatedItemsView.TemplatedItems;
			Tuple<int, int> location = templatedItems.GetGroupAndIndexOfItem(group, item);
			if (location.Item1 == -1 || location.Item2 == -1)
				return;

			object[] t = templatedItems.GetGroup(location.Item1).ItemsSource.Cast<object>().ToArray();
			object c = t[location.Item2];

			double viewportHeight = viewer.ViewportHeight;

			var semanticLocation = new SemanticZoomLocation { Item = c };

			switch (toPosition)
			{
				case ScrollToPosition.Start:
					{
						List.ScrollIntoView(c, ScrollIntoViewAlignment.Leading);
						return;
					}

				case ScrollToPosition.MakeVisible:
					{
						List.ScrollIntoView(c, ScrollIntoViewAlignment.Default);
						return;
					}

				case ScrollToPosition.End:
				case ScrollToPosition.Center:
					{
						var content = (FrameworkElement)List.ItemTemplate.LoadContent();
						content.DataContext = c;
						content.Measure(new Windows.Foundation.Size(viewer.ActualWidth, double.PositiveInfinity));

						double tHeight = content.DesiredSize.Height;

						if (toPosition == ScrollToPosition.Center)
							semanticLocation.Bounds = new Rect(0, viewportHeight / 2 - tHeight / 2, 0, 0);
						else
							semanticLocation.Bounds = new Rect(0, viewportHeight - tHeight, 0, 0);

						break;
					}
			}

			// Waiting for loaded doesn't seem to be enough anymore; the ScrollViewer does not appear until after Loaded.
			// Even if the ScrollViewer is present, an invoke at low priority fails (E_FAIL) presumably because the items are
			// still loading. An invoke at idle sometimes work, but isn't reliable enough, so we'll just have to commit
			// treason and use a blanket catch for the E_FAIL and try again.
			try
			{
				List.MakeVisible(semanticLocation);
			}
			catch (Exception)
			{
				if (previouslyFailed)
					return;

				Task.Delay(1).ContinueWith(ct => { ScrollTo(group, item, toPosition, shouldAnimate, includeGroup, true); }, TaskScheduler.FromCurrentSynchronizationContext()).WatchForError();
			}
		}

		void OnElementScrollToRequested(object sender, ScrollToRequestedEventArgs e)
		{
			var scrollArgs = (ITemplatedItemsListScrollToRequestedEventArgs)e;
			ScrollTo(scrollArgs.Group, scrollArgs.Item, e.Position, e.ShouldAnimate);
		}

		T GetFirstDescendant<T>(DependencyObject element) where T : FrameworkElement
		{
			int count = VisualTreeHelper.GetChildrenCount(element);
			for (var i = 0; i < count; i++)
			{
				DependencyObject child = VisualTreeHelper.GetChild(element, i);

				T target = child as T ?? GetFirstDescendant<T>(child);
				if (target != null)
					return target;
			}

			return null;
		}

		ContentControl GetHeaderControl()
		{
			if (_headerControl == null)
			{
				ScrollViewer viewer = GetScrollViewer();
				if (viewer == null)
					return null;

				var presenter = GetFirstDescendant<ItemsPresenter>(viewer);
				if (presenter == null)
					return null;

				_headerControl = GetFirstDescendant<ContentControl>(presenter);
			}

			return _headerControl;
		}

		ScrollViewer GetScrollViewer()
		{
			if (_scrollViewer == null)
			{
				_scrollViewer = List.GetFirstDescendant<ScrollViewer>();
			}

			return _scrollViewer;
		}

		void ListOnTapped(object sender, TappedRoutedEventArgs args)
		{
			var orig = args.OriginalSource as DependencyObject;
			int index = -1;

			// Work our way up the tree until we find the actual list item
			// the user tapped on

			while (orig != null && orig != List)
			{
				var lv = orig as ListViewItem;
				if (lv != null)
				{
					index = TemplatedItemsView.TemplatedItems.GetGlobalIndexOfItem(lv.Content);
					break;
				}

				orig = VisualTreeHelper.GetParent(orig);
			}

			if (index > -1)
			{
				OnListItemClicked(index);
			}
		}

		void OnElementItemSelected(object sender, SelectedItemChangedEventArgs e)
		{
			if (Element == null)
				return;

			if (_deferSelection)
			{
				// If we get more than one of these, that's okay; we only want the latest one
				_deferredSelectedItemChangedEvent = new Tuple<object, SelectedItemChangedEventArgs>(sender, e);
				return;
			}

			if (e.SelectedItem == null)
			{
				List.SelectedIndex = -1;
				return;
			}
			var templatedItems = TemplatedItemsView.TemplatedItems;
			var index = 0;
			if (Element.IsGroupingEnabled)
			{
				int selectedItemIndex = templatedItems.GetGlobalIndexOfItem(e.SelectedItem);
				var leftOver = 0;
				int groupIndex = templatedItems.GetGroupIndexFromGlobal(selectedItemIndex, out leftOver);

				index = selectedItemIndex - (groupIndex + 1);
			}
			else
			{
				index = templatedItems.GetGlobalIndexOfItem(e.SelectedItem);
			}

			List.SelectedIndex = index;
		}

		void OnListItemClicked(int index)
		{
#if !WINDOWS_UWP
			// If we're on the phone , we need to cache the selected item in case the handler 
			// we're about to call changes any item indexes;
			// in some cases, those index changes will throw an exception we can't catch if 
			// the listview has an item selected
			object selectedItem = null;
			if (Device.Idiom == TargetIdiom.Phone)
			{
				selectedItem = List.SelectedItem;
				List.SelectedIndex = -1;
				_deferSelection = true;
			}
#endif

			Element.NotifyRowTapped(index, cell: null);
			_itemWasClicked = true;

#if !WINDOWS_UWP

			if (Device.Idiom != TargetIdiom.Phone || List == null)
			{
				return;
			}

			_deferSelection = false;

			if (_deferredSelectedItemChangedEvent != null)
			{
				// If there was a selection change attempt while RowTapped was being handled, replay it
				OnElementItemSelected(_deferredSelectedItemChangedEvent.Item1, _deferredSelectedItemChangedEvent.Item2);
				_deferredSelectedItemChangedEvent = null;
			}
			else if (List?.SelectedIndex == -1 && selectedItem != null)
			{
				// Otherwise, set the selection back to whatever it was before all this started
				List.SelectedItem = selectedItem;
			}
#endif
		}

		void OnListItemClicked(object sender, ItemClickEventArgs e)
		{
			if (e.ClickedItem != null)
				OnListItemClicked(((WListView)e.OriginalSource).Items.IndexOf(e.ClickedItem));
		}

		void OnControlSelectionChanged(object sender, SelectionChangedEventArgs e)
		{
#if !WINDOWS_UWP
			RestorePreviousSelectedVisual();

			if (e.AddedItems.Count == 0)
			{
				// Deselecting an item is a valid SelectedItem change.
				if (Element.SelectedItem != List.SelectedItem)
				{
					OnListItemClicked(List.SelectedIndex);
				}

				return;
			}

			object cell = e.AddedItems[0];
			if (cell == null)
				return;

			if (Device.Idiom == TargetIdiom.Phone)
			{
				FrameworkElement element = FindElement(cell);
				if (element != null)
				{
					SetSelectedVisual(element);
				}
			}
#endif
			if (Element.SelectedItem != List.SelectedItem && !_itemWasClicked)
				((IElementController)Element).SetValueFromRenderer(ListView.SelectedItemProperty, List.SelectedItem);

			_itemWasClicked = false;
		}

		FrameworkElement FindElement(object cell)
		{
			foreach (CellControl selector in FindDescendants<CellControl>(List))
			{
				if (ReferenceEquals(cell, selector.DataContext))
					return selector;
			}

			return null;
		}

#if !WINDOWS_UWP

		void RestorePreviousSelectedVisual()
		{
			foreach (BrushedElement highlight in _highlightedElements)
			{
				if (highlight.IsBound)
				{
					highlight.Element.SetForeground(highlight.BrushBinding);
				}
				else
				{
					highlight.Element.SetForeground(highlight.Brush);
				}
			}

			_highlightedElements.Clear();
		}

		void SetSelectedVisual(FrameworkElement element)
		{
			// Find all labels in children and set their foreground color to accent color
			IEnumerable<FrameworkElement> elementsToHighlight = FindPhoneHighlights(element);
			var systemAccentBrush = (Brush)WApp.Current.Resources["SystemColorControlAccentBrush"];

			foreach (FrameworkElement toHighlight in elementsToHighlight)
			{
				Brush brush = null;
				WBinding binding = toHighlight.GetForegroundBinding();
				if (binding == null)
					brush = toHighlight.GetForeground();

				var brushedElement = new BrushedElement(toHighlight, binding, brush);
				_highlightedElements.Add(brushedElement);

				toHighlight.SetForeground(systemAccentBrush);
			}
		}

		IEnumerable<FrameworkElement> FindPhoneHighlights(FrameworkElement element)
		{
			FrameworkElement parent = element;
			while (true)
			{
				element = parent;
				if (element is CellControl)
					break;

				parent = VisualTreeHelper.GetParent(element) as FrameworkElement;
				if (parent == null)
				{
					parent = element;
					break;
				}
			}

			return FindPhoneHighlightCore(parent);
		}

		IEnumerable<FrameworkElement> FindPhoneHighlightCore(DependencyObject element)
		{
			int children = VisualTreeHelper.GetChildrenCount(element);
			for (var i = 0; i < children; i++)
			{
				DependencyObject child = VisualTreeHelper.GetChild(element, i);

				var label = child as LabelRenderer;
				var childElement = child as FrameworkElement;
				if (childElement != null && (GetHighlightWhenSelected(childElement) || label != null))
				{
					if (label != null)
						yield return label.Control;
					else
						yield return childElement;
				}

				foreach (FrameworkElement recursedElement in FindPhoneHighlightCore(childElement))
					yield return recursedElement;
			}
		}
#endif

		bool _deferSelection = false;
		Tuple<object, SelectedItemChangedEventArgs> _deferredSelectedItemChangedEvent;

#if WINDOWS_UWP

		class FormsListViewAutomationPeer : ListViewAutomationPeer
		{
			public FormsListViewAutomationPeer(WListView owner) : base(owner)
			{
			}

			protected override ItemAutomationPeer OnCreateItemAutomationPeer(object item)
			{
				if (item is string || item.GetType().GetTypeInfo().IsValueType)
				{
					// Passing a string or value type to the base method will cause automation to 
					// freeze entirely whenever the ListView is on screen. In other words, if the 
					// list is bound to an ItemSource which is made up only of, e.g., 
					// IEnumerable<string> or IEnumerable<int>, it'll break automation

					// Returning null here prevents the freeze. There's probably a more correct
					// solution to this problem. 
					
					return null;
				}

				return base.OnCreateItemAutomationPeer(item);
			}
		}

		protected override AutomationPeer OnCreateAutomationPeer()
		{
			return List == null 
				? new FrameworkElementAutomationPeer(this) 
				: new FormsListViewAutomationPeer(List);
		}

#endif
	}
}