using System;
using System.Collections;
using System.Collections.Generic;
using OpenTK.Mathematics;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace LELEngine.UIElements
{
	public enum SelectionType
	{
		None,
		Single,
		Multiple
	}

	/// <summary>
	///     A virtualized list over <see cref="itemsSource" />: rows of <see cref="fixedItemHeight" /> are created with
	///     <see cref="makeItem" />, bound with <see cref="bindItem" /> and recycled while scrolling, so a list of a
	///     million items costs as much as the rows on screen. Supports single and multiple selection with the mouse
	///     and the keyboard.
	/// </summary>
	[UxmlElement]
	public class ListView : VisualElement
	{
		#region PublicFields

		public static readonly string ussClassName = "unity-list-view";
		public static readonly string itemUssClassName = ussClassName + "__item";
		public static readonly string itemSelectedVariantUssClassName = itemUssClassName + "--selected";
		public static readonly string itemAlternativeBackgroundUssClassName = itemUssClassName + "--alternative-background";
		public static readonly string emptyLabelUssClassName = ussClassName + "__empty-label";
		public static readonly string borderUssClassName = ussClassName + "--with-border";

		public event Action<IEnumerable<object>> selectionChanged;
		public event Action<IEnumerable<int>> selectedIndicesChanged;
		public event Action<IEnumerable<object>> itemsChosen;

		public IList itemsSource
		{
			get => m_ItemsSource;
			set
			{
				m_ItemsSource = value;
				selected.Clear();
				RefreshItems();
			}
		}

		public Func<VisualElement> makeItem
		{
			get => m_MakeItem;
			set
			{
				m_MakeItem = value;
				Rebuild();
			}
		}

		public Action<VisualElement, int> bindItem
		{
			get => m_BindItem;
			set
			{
				m_BindItem = value;
				RefreshItems();
			}
		}

		public Action<VisualElement, int> unbindItem { get; set; }
		public Action<VisualElement> destroyItem { get; set; }

		[UxmlAttribute("fixed-item-height")]
		public float fixedItemHeight
		{
			get => m_ItemHeight;
			set
			{
				m_ItemHeight = Math.Max(1f, value);
				RefreshItems();
			}
		}

		[UxmlAttribute("selection-type")]
		public SelectionType selectionType
		{
			get => m_SelectionType;
			set
			{
				m_SelectionType = value;
				if (value == SelectionType.None)
				{
					ClearSelection();
				}
				else if (value == SelectionType.Single && selected.Count > 1)
				{
					SetSelection(selected[0]);
				}
			}
		}

		[UxmlAttribute("show-border")]
		public bool showBorder
		{
			get => ClassListContains(borderUssClassName);
			set => EnableInClassList(borderUssClassName, value);
		}

		[UxmlAttribute("show-alternating-row-backgrounds")]
		public bool showAlternatingRowBackgrounds
		{
			get => m_Alternating;
			set
			{
				m_Alternating = value;
				RefreshItems();
			}
		}

		public int selectedIndex
		{
			get => selected.Count > 0 ? selected[0] : -1;
			set => SetSelection(value);
		}

		public IEnumerable<int> selectedIndices => selected;
		public object selectedItem => selected.Count > 0 && m_ItemsSource != null && selected[0] < m_ItemsSource.Count ? m_ItemsSource[selected[0]] : null;

		public IEnumerable<object> selectedItems
		{
			get
			{
				foreach (int index in selected)
				{
					if (m_ItemsSource != null && index >= 0 && index < m_ItemsSource.Count)
					{
						yield return m_ItemsSource[index];
					}
				}
			}
		}

		public ScrollView scrollView { get; }
		public int itemCount => m_ItemsSource?.Count ?? 0;

		#endregion

		#region PrivateFields

		private IList m_ItemsSource;
		private Func<VisualElement> m_MakeItem;
		private Action<VisualElement, int> m_BindItem;
		private float m_ItemHeight = 22f;
		private SelectionType m_SelectionType = SelectionType.Single;
		private bool m_Alternating;
		private readonly List<int> selected = new List<int>();
		private readonly VisualElement content;
		private readonly Label emptyLabel;
		private readonly List<Row> rows = new List<Row>();
		private readonly Stack<Row> pool = new Stack<Row>();
		private int firstVisible = -1;
		private int lastClickIndex = -1;
		private long lastClickTime;

		private sealed class Row
		{
			public VisualElement container;
			public VisualElement item;
			public int index = -1;
		}

		#endregion

		#region Constructors

		public ListView()
		{
			AddToClassList(ussClassName);
			focusable = true;
			tabIndex = 0;
			scrollView = new ScrollView(ScrollViewMode.Vertical) { name = "unity-content-scroll-view" };
			scrollView.verticalScroller.valueChanged += value => RefreshVisible();
			hierarchy.Add(scrollView);
			content = scrollView.contentContainer;
			emptyLabel = new Label("List is empty") { pickingMode = PickingMode.Ignore };
			emptyLabel.AddToClassList(emptyLabelUssClassName);
			emptyLabel.style.display = DisplayStyle.None;
			hierarchy.Add(emptyLabel);
			scrollView.contentViewport.RegisterCallback<GeometryChangedEvent>(evt => RefreshVisible());
			RegisterCallback<KeyDownEvent>(OnKeyDown);
		}

		public ListView(IList itemsSource, float itemHeight = 22f, Func<VisualElement> makeItem = null, Action<VisualElement, int> bindItem = null)
			: this()
		{
			m_ItemHeight = itemHeight;
			m_MakeItem = makeItem;
			m_BindItem = bindItem;
			this.itemsSource = itemsSource;
		}

		#endregion

		#region PublicMethods

		/// <summary>Re-binds the visible rows (the source's contents changed).</summary>
		public void RefreshItems()
		{
			int count = itemCount;
			content.style.height = count * m_ItemHeight;
			emptyLabel.style.display = count == 0 ? DisplayStyle.Flex : DisplayStyle.None;
			selected.RemoveAll(index => index < 0 || index >= count);
			firstVisible = -1;
			RefreshVisible();
		}

		public void RefreshItem(int index)
		{
			foreach (Row row in rows)
			{
				if (row.index == index)
				{
					Bind(row, index);
				}
			}
		}

		/// <summary>Recreates every row (makeItem changed).</summary>
		public void Rebuild()
		{
			foreach (Row row in rows)
			{
				Release(row);
			}

			rows.Clear();
			while (pool.Count > 0)
			{
				Row row = pool.Pop();
				destroyItem?.Invoke(row.item);
			}

			content.Clear();
			RefreshItems();
		}

		public void ScrollToItem(int index)
		{
			if (index < 0 || index >= itemCount)
			{
				return;
			}

			float viewportHeight = scrollView.contentViewport.contentRect.height;
			float top = index * m_ItemHeight;
			Vector2 offset = scrollView.scrollOffset;
			if (top < offset.Y) offset.Y = top;
			else if (top + m_ItemHeight > offset.Y + viewportHeight) offset.Y = top + m_ItemHeight - viewportHeight;
			scrollView.scrollOffset = offset;
		}

		public void SetSelection(int index)
		{
			SetSelection(index >= 0 ? new[] { index } : Array.Empty<int>());
		}

		public void SetSelection(IEnumerable<int> indices)
		{
			SetSelectionInternal(indices, true);
		}

		public void SetSelectionWithoutNotify(IEnumerable<int> indices)
		{
			SetSelectionInternal(indices, false);
		}

		public void AddToSelection(int index)
		{
			if (index < 0 || index >= itemCount || selected.Contains(index) || m_SelectionType == SelectionType.None)
			{
				return;
			}

			if (m_SelectionType == SelectionType.Single)
			{
				selected.Clear();
			}

			selected.Add(index);
			UpdateSelectionVisuals();
			NotifySelection();
		}

		public void RemoveFromSelection(int index)
		{
			if (selected.Remove(index))
			{
				UpdateSelectionVisuals();
				NotifySelection();
			}
		}

		public void ClearSelection()
		{
			if (selected.Count == 0)
			{
				return;
			}

			selected.Clear();
			UpdateSelectionVisuals();
			NotifySelection();
		}

		#endregion

		#region PrivateMethods

		private void SetSelectionInternal(IEnumerable<int> indices, bool notify)
		{
			selected.Clear();
			if (m_SelectionType != SelectionType.None)
			{
				foreach (int index in indices)
				{
					if (index >= 0 && index < itemCount && !selected.Contains(index))
					{
						selected.Add(index);
						if (m_SelectionType == SelectionType.Single)
						{
							break;
						}
					}
				}
			}

			UpdateSelectionVisuals();
			if (notify)
			{
				NotifySelection();
			}
		}

		private void NotifySelection()
		{
			selectedIndicesChanged?.Invoke(selected);
			selectionChanged?.Invoke(selectedItems);
		}

		private void RefreshVisible()
		{
			int count = itemCount;
			float viewportHeight = scrollView.contentViewport.contentRect.height;
			float offset = scrollView.scrollOffset.Y;
			int first = Math.Max(0, (int)Math.Floor(offset / m_ItemHeight));
			int visible = (int)Math.Ceiling(viewportHeight / m_ItemHeight) + 2;
			int last = Math.Min(count, first + visible);

			// Recycle rows outside the range, bind the missing ones.
			for (int i = rows.Count - 1; i >= 0; i--)
			{
				Row row = rows[i];
				if (row.index < first || row.index >= last)
				{
					Release(row);
					rows.RemoveAt(i);
				}
			}

			for (int index = first; index < last; index++)
			{
				bool present = false;
				foreach (Row row in rows)
				{
					if (row.index == index)
					{
						present = true;
						if (firstVisible < 0)
						{
							Bind(row, index);
						}

						break;
					}
				}

				if (!present)
				{
					Row row = Acquire();
					rows.Add(row);
					Bind(row, index);
				}
			}

			firstVisible = first;
		}

		private Row Acquire()
		{
			Row row;
			if (pool.Count > 0)
			{
				row = pool.Pop();
				row.container.style.display = DisplayStyle.Flex;
				return row;
			}

			row = new Row { container = new VisualElement() };
			row.container.AddToClassList(itemUssClassName);
			row.container.style.position = Position.Absolute;
			row.container.style.left = 0f;
			row.container.style.right = 0f;
			row.container.style.height = m_ItemHeight;
			row.item = m_MakeItem != null ? m_MakeItem() : new Label();
			row.container.Add(row.item);
			Row captured = row;
			row.container.RegisterCallback<PointerDownEvent>(evt => OnRowPointerDown(captured, evt));
			content.Add(row.container);
			return row;
		}

		private void Release(Row row)
		{
			if (row.index >= 0)
			{
				unbindItem?.Invoke(row.item, row.index);
			}

			row.index = -1;
			row.container.style.display = DisplayStyle.None;
			pool.Push(row);
		}

		private void Bind(Row row, int index)
		{
			if (row.index != index && row.index >= 0)
			{
				unbindItem?.Invoke(row.item, row.index);
			}

			row.index = index;
			row.container.style.top = index * m_ItemHeight;
			row.container.style.height = m_ItemHeight;
			row.container.EnableInClassList(itemSelectedVariantUssClassName, selected.Contains(index));
			row.container.EnableInClassList(itemAlternativeBackgroundUssClassName, m_Alternating && index % 2 == 1);
			if (m_BindItem != null)
			{
				m_BindItem(row.item, index);
			}
			else if (row.item is Label label && m_ItemsSource != null && index < m_ItemsSource.Count)
			{
				label.text = m_ItemsSource[index]?.ToString() ?? "";
			}
		}

		private void UpdateSelectionVisuals()
		{
			foreach (Row row in rows)
			{
				row.container.EnableInClassList(itemSelectedVariantUssClassName, selected.Contains(row.index));
			}
		}

		private void OnRowPointerDown(Row row, PointerDownEvent evt)
		{
			if (evt.button != 0 || row.index < 0 || !enabledInHierarchy)
			{
				return;
			}

			Focus();
			long now = elementPanel?.timeMs ?? 0;
			bool doubleClick = row.index == lastClickIndex && now - lastClickTime < 400;
			lastClickIndex = row.index;
			lastClickTime = now;

			if (m_SelectionType == SelectionType.Multiple && evt.ctrlKey)
			{
				if (selected.Contains(row.index)) RemoveFromSelection(row.index);
				else AddToSelection(row.index);
			}
			else if (m_SelectionType == SelectionType.Multiple && evt.shiftKey && selected.Count > 0)
			{
				int anchor = selected[0];
				var range = new List<int>();
				for (int i = Math.Min(anchor, row.index); i <= Math.Max(anchor, row.index); i++) range.Add(i);
				SetSelection(range);
			}
			else if (m_SelectionType != SelectionType.None)
			{
				SetSelection(row.index);
			}

			if (doubleClick)
			{
				itemsChosen?.Invoke(selectedItems);
			}

			evt.StopPropagation();
		}

		private void OnKeyDown(KeyDownEvent evt)
		{
			if (itemCount == 0 || m_SelectionType == SelectionType.None)
			{
				return;
			}

			int current = selectedIndex;
			switch (evt.keyCode)
			{
				case Keys.Up:
					SetSelection(Math.Max(0, current - 1));
					ScrollToItem(selectedIndex);
					break;
				case Keys.Down:
					SetSelection(Math.Min(itemCount - 1, current + 1));
					ScrollToItem(selectedIndex);
					break;
				case Keys.Home:
					SetSelection(0);
					ScrollToItem(0);
					break;
				case Keys.End:
					SetSelection(itemCount - 1);
					ScrollToItem(itemCount - 1);
					break;
				case Keys.Enter:
				case Keys.KeyPadEnter:
					if (selected.Count > 0) itemsChosen?.Invoke(selectedItems);
					break;
				case Keys.A when evt.ctrlKey && m_SelectionType == SelectionType.Multiple:
				{
					var all = new List<int>();
					for (int i = 0; i < itemCount; i++) all.Add(i);
					SetSelection(all);
					break;
				}

				default:
					return;
			}

			evt.StopPropagation();
		}

		#endregion
	}
}
