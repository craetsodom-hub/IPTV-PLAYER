using System;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace IptvPlayer.App.Controls;

public sealed class VirtualizingUniformGrid : VirtualizingPanel, IScrollInfo
{
	public static readonly DependencyProperty ColumnsProperty = DependencyProperty.Register("Columns", typeof(int), typeof(VirtualizingUniformGrid), new FrameworkPropertyMetadata(1, FrameworkPropertyMetadataOptions.AffectsMeasure, null, CoerceColumns));

	private Size _extent;

	private Size _viewport;

	private Point _offset;

	private double _rowHeight;

	private double _measuredCellWidth;

	public int Columns
	{
		get
		{
			return (int)GetValue(ColumnsProperty);
		}
		set
		{
			SetValue(ColumnsProperty, value);
		}
	}

	public bool CanHorizontallyScroll { get; set; }

	public bool CanVerticallyScroll { get; set; }

	public double ExtentHeight => _extent.Height;

	public double ExtentWidth => _extent.Width;

	public double HorizontalOffset => _offset.X;

	public ScrollViewer? ScrollOwner { get; set; }

	public double VerticalOffset => _offset.Y;

	public double ViewportHeight => _viewport.Height;

	public double ViewportWidth => _viewport.Width;

	protected override Size MeasureOverride(Size availableSize)
	{
		ItemsControl itemsOwner = ItemsControl.GetItemsOwner(this);
		int num = itemsOwner?.Items.Count ?? 0;
		double num2 = ResolveViewportLength(availableSize.Width, _viewport.Width, ScrollOwner?.ActualWidth ?? 0.0);
		double num3 = ResolveViewportLength(availableSize.Height, _viewport.Height, ScrollOwner?.ActualHeight ?? 0.0);
		if (num == 0)
		{
			if (base.InternalChildren.Count > 0)
			{
				RemoveInternalChildRange(0, base.InternalChildren.Count);
			}
			_rowHeight = 0.0;
			UpdateScrollInfo(new Size(num2, 0.0), new Size(num2, num3));
			return new Size(num2, num3);
		}
		if (num2 <= 0.0)
		{
			UpdateScrollInfo(new Size(0.0, 0.0), new Size(0.0, num3));
			return new Size(0.0, num3);
		}
		double num4 = num2 / (double)Columns;
		if (!AreClose(num4, _measuredCellWidth))
		{
			_measuredCellWidth = num4;
			_rowHeight = 0.0;
		}
		EnsureRowHeight(num, num4);
		if (_rowHeight <= 0.0)
		{
			UpdateScrollInfo(new Size(num2, 0.0), new Size(num2, num3));
			return new Size(num2, num3);
		}
		int rowCount = GetRowCount(num);
		UpdateScrollInfo(new Size(num2, GetExtentHeight(num)), new Size(num2, num3));
		VirtualizationCacheLength cacheLength = VirtualizingPanel.GetCacheLength(itemsOwner);
		VirtualizationCacheLengthUnit cacheLengthUnit = VirtualizingPanel.GetCacheLengthUnit(itemsOwner);
		double num5 = ToPixels(cacheLength.CacheBeforeViewport, cacheLengthUnit, num3, _rowHeight);
		double num6 = ToPixels(cacheLength.CacheAfterViewport, cacheLengthUnit, num3, _rowHeight);
		int num7 = Math.Max(0, (int)Math.Floor((_offset.Y - num5) / _rowHeight));
		double num8 = Math.Max(0.0, _offset.Y + num3 + num6 - double.Epsilon);
		int num9 = Math.Min(rowCount - 1, (int)Math.Floor(num8 / _rowHeight));
		int firstIndex = num7 * Columns;
		int lastIndex = Math.Min(num - 1, (num9 + 1) * Columns - 1);
		RealizeRange(firstIndex, lastIndex, num4);
		RecycleOutsideRange(firstIndex, lastIndex);
		return new Size(num2, num3);
	}

	protected override Size ArrangeOverride(Size finalSize)
	{
		UpdateScrollInfo(viewport: new Size(Math.Max(0.0, finalSize.Width), Math.Max(0.0, finalSize.Height)), extent: _extent);
		if (_rowHeight <= 0.0 || Columns <= 0)
		{
			return finalSize;
		}
		double num = finalSize.Width / (double)Columns;
		ItemsControl itemsOwner = ItemsControl.GetItemsOwner(this);
		foreach (UIElement internalChild in base.InternalChildren)
		{
			int num2 = itemsOwner?.ItemContainerGenerator.IndexFromContainer(internalChild) ?? (-1);
			if (num2 >= 0)
			{
				int num3 = num2 / Columns;
				int num4 = num2 % Columns;
				internalChild.Arrange(new Rect((double)num4 * num, (double)num3 * _rowHeight - _offset.Y, num, _rowHeight));
			}
		}
		return finalSize;
	}

	protected override void OnItemsChanged(object sender, ItemsChangedEventArgs args)
	{
		base.OnItemsChanged(sender, args);
		if (args.Action == NotifyCollectionChangedAction.Reset)
		{
			_rowHeight = 0.0;
			_measuredCellWidth = 0.0;
		}
		InvalidateMeasure();
		ScrollOwner?.InvalidateScrollInfo();
	}

	protected override void OnClearChildren()
	{
		base.OnClearChildren();
		_rowHeight = 0.0;
		_measuredCellWidth = 0.0;
	}

	public void LineDown()
	{
		SetVerticalOffset(VerticalOffset + 16.0);
	}

	public void LineLeft()
	{
		SetHorizontalOffset(HorizontalOffset - 16.0);
	}

	public void LineRight()
	{
		SetHorizontalOffset(HorizontalOffset + 16.0);
	}

	public void LineUp()
	{
		SetVerticalOffset(VerticalOffset - 16.0);
	}

	public Rect MakeVisible(Visual visual, Rect rectangle)
	{
		if (!(visual is UIElement container))
		{
			return rectangle;
		}
		int num = ItemsControl.GetItemsOwner(this)?.ItemContainerGenerator.IndexFromContainer(container) ?? (-1);
		if (num < 0 || _rowHeight <= 0.0)
		{
			return rectangle;
		}
		double num2 = (double)(num / Columns) * _rowHeight;
		double num3 = num2 + _rowHeight;
		if (num2 < VerticalOffset)
		{
			SetVerticalOffset(num2);
		}
		else if (num3 > VerticalOffset + ViewportHeight)
		{
			SetVerticalOffset(num3 - ViewportHeight);
		}
		return new Rect(0.0, num2 - VerticalOffset, ViewportWidth, _rowHeight);
	}

	public void MouseWheelDown()
	{
		SetVerticalOffset(VerticalOffset + 48.0);
	}

	public void MouseWheelLeft()
	{
		SetHorizontalOffset(HorizontalOffset - 48.0);
	}

	public void MouseWheelRight()
	{
		SetHorizontalOffset(HorizontalOffset + 48.0);
	}

	public void MouseWheelUp()
	{
		SetVerticalOffset(VerticalOffset - 48.0);
	}

	public void PageDown()
	{
		SetVerticalOffset(VerticalOffset + ViewportHeight);
	}

	public void PageLeft()
	{
		SetHorizontalOffset(HorizontalOffset - ViewportWidth);
	}

	public void PageRight()
	{
		SetHorizontalOffset(HorizontalOffset + ViewportWidth);
	}

	public void PageUp()
	{
		SetVerticalOffset(VerticalOffset - ViewportHeight);
	}

	public void SetHorizontalOffset(double offset)
	{
		double num = (CanHorizontallyScroll ? ClampOffset(offset, ExtentWidth, ViewportWidth) : 0.0);
		if (!AreClose(num, _offset.X))
		{
			_offset.X = num;
			ScrollOwner?.InvalidateScrollInfo();
			InvalidateArrange();
		}
	}

	public void SetVerticalOffset(double offset)
	{
		double num = (CanVerticallyScroll ? ClampOffset(offset, ExtentHeight, ViewportHeight) : 0.0);
		if (!AreClose(num, _offset.Y))
		{
			_offset.Y = num;
			ScrollOwner?.InvalidateScrollInfo();
			InvalidateMeasure();
		}
	}

	private static object CoerceColumns(DependencyObject dependencyObject, object value)
	{
		return Math.Max(1, (int)value);
	}

	private static double ClampOffset(double offset, double extent, double viewport)
	{
		if (double.IsNaN(offset))
		{
			return 0.0;
		}
		return Math.Clamp(offset, 0.0, Math.Max(0.0, extent - viewport));
	}

	private static bool AreClose(double left, double right)
	{
		return Math.Abs(left - right) < 0.001;
	}

	private static double ResolveViewportLength(double available, double previousViewport, double ownerActual)
	{
		if (double.IsFinite(available))
		{
			return Math.Max(0.0, available);
		}
		if (double.IsFinite(previousViewport) && previousViewport > 0.0)
		{
			return previousViewport;
		}
		if (!double.IsFinite(ownerActual))
		{
			return 0.0;
		}
		return Math.Max(0.0, ownerActual);
	}

	private static int CalculateRowCount(int itemCount, int columns)
	{
		if (itemCount > 0)
		{
			return (itemCount + columns - 1) / columns;
		}
		return 0;
	}

	private static double CalculateExtentHeight(int itemCount, int columns, double rowHeight)
	{
		return (double)CalculateRowCount(itemCount, columns) * rowHeight;
	}

	private int GetRowCount(int itemCount)
	{
		return CalculateRowCount(itemCount, Columns);
	}

	private double GetExtentHeight(int itemCount)
	{
		return CalculateExtentHeight(itemCount, Columns, _rowHeight);
	}

	private static double ToPixels(double cacheLength, VirtualizationCacheLengthUnit unit, double viewportHeight, double rowHeight)
	{
		return unit switch
		{
			VirtualizationCacheLengthUnit.Page => cacheLength * viewportHeight, 
			VirtualizationCacheLengthUnit.Pixel => cacheLength, 
			VirtualizationCacheLengthUnit.Item => cacheLength * rowHeight, 
			_ => 0.0, 
		};
	}

	private void EnsureRowHeight(int itemCount, double cellWidth)
	{
		if (_rowHeight > 0.0 || itemCount == 0)
		{
			return;
		}
		IItemContainerGenerator itemContainerGenerator = GetItemContainerGenerator();
		GeneratorPosition position = itemContainerGenerator.GeneratorPositionFromIndex(0);
		using (itemContainerGenerator.StartAt(position, GeneratorDirection.Forward, allowStartAtRealizedItem: true))
		{
			UIElement uIElement = (UIElement)itemContainerGenerator.GenerateNext(out var isNewlyRealized);
			if (isNewlyRealized || VisualTreeHelper.GetParent(uIElement) == null)
			{
				AddInternalChild(uIElement);
				if (isNewlyRealized)
				{
					itemContainerGenerator.PrepareItemContainer(uIElement);
				}
			}
			uIElement.Measure(new Size(cellWidth, double.PositiveInfinity));
			_rowHeight = uIElement.DesiredSize.Height;
		}
	}

	private void RealizeRange(int firstIndex, int lastIndex, double cellWidth)
	{
		if (firstIndex > lastIndex)
		{
			return;
		}
		IItemContainerGenerator itemContainerGenerator = GetItemContainerGenerator();
		GeneratorPosition position = itemContainerGenerator.GeneratorPositionFromIndex(firstIndex);
		int num = ((position.Offset == 0) ? position.Index : (position.Index + 1));
		using (itemContainerGenerator.StartAt(position, GeneratorDirection.Forward, allowStartAtRealizedItem: true))
		{
			int num2 = firstIndex;
			while (num2 <= lastIndex)
			{
				UIElement uIElement = (UIElement)itemContainerGenerator.GenerateNext(out var isNewlyRealized);
				if (isNewlyRealized || VisualTreeHelper.GetParent(uIElement) == null)
				{
					if (num >= base.InternalChildren.Count)
					{
						AddInternalChild(uIElement);
					}
					else
					{
						InsertInternalChild(num, uIElement);
					}
					if (isNewlyRealized)
					{
						itemContainerGenerator.PrepareItemContainer(uIElement);
					}
				}
				uIElement.Measure(new Size(cellWidth, double.PositiveInfinity));
				num2++;
				num++;
			}
		}
	}

	private void RecycleOutsideRange(int firstIndex, int lastIndex)
	{
		IItemContainerGenerator itemContainerGenerator = GetItemContainerGenerator();
		ItemContainerGenerator itemContainerGenerator2 = ItemsControl.GetItemsOwner(this).ItemContainerGenerator;
		for (int num = base.InternalChildren.Count - 1; num >= 0; num--)
		{
			UIElement container = base.InternalChildren[num];
			int num2 = itemContainerGenerator2.IndexFromContainer(container);
			if (num2 < firstIndex || num2 > lastIndex)
			{
				if (num2 < 0)
				{
					RemoveInternalChildRange(num, 1);
				}
				else
				{
					GeneratorPosition position = itemContainerGenerator.GeneratorPositionFromIndex(num2);
					if (itemContainerGenerator is IRecyclingItemContainerGenerator recyclingItemContainerGenerator)
					{
						recyclingItemContainerGenerator.Recycle(position, 1);
					}
					else
					{
						itemContainerGenerator.Remove(position, 1);
					}
					RemoveInternalChildRange(num, 1);
				}
			}
		}
	}

	private void UpdateScrollInfo(Size extent, Size viewport)
	{
		bool num = !AreClose(extent.Width, _extent.Width) || !AreClose(extent.Height, _extent.Height) || !AreClose(viewport.Width, _viewport.Width) || !AreClose(viewport.Height, _viewport.Height);
		_extent = extent;
		_viewport = viewport;
		_offset.X = ClampOffset(_offset.X, ExtentWidth, ViewportWidth);
		_offset.Y = ClampOffset(_offset.Y, ExtentHeight, ViewportHeight);
		if (num)
		{
			ScrollOwner?.InvalidateScrollInfo();
		}
	}

	private IItemContainerGenerator GetItemContainerGenerator()
	{
		return base.ItemContainerGenerator ?? ItemsControl.GetItemsOwner(this)?.ItemContainerGenerator ?? throw new InvalidOperationException("VirtualizingUniformGrid must be used as an ItemsControl items host.");
	}
}
