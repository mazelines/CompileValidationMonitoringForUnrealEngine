using System.Collections.Concurrent;
using System.Drawing.Drawing2D;
using System.Text;
using ShipCheck.Core;

namespace ShipCheckMonitor;

static class HealthStyle
{
	public static Color ColorOf(Health health) => health switch
	{
		Health.Running => Color.FromArgb(0x2F, 0x80, 0xED),
		Health.NoChanges => Color.FromArgb(0x8A, 0x94, 0xA6),
		Health.Passed => Color.FromArgb(0x27, 0xAE, 0x60),
		Health.NeedsValidation => Color.FromArgb(0xF2, 0x99, 0x4A),
		Health.Failed => Color.FromArgb(0xEB, 0x57, 0x57),
		Health.Error => Color.FromArgb(0xEB, 0x57, 0x57),
		_ => Color.FromArgb(0xBD, 0xBD, 0xBD),
	};

	public static string GlyphOf(Health health) => health switch
	{
		Health.Running => "…",
		Health.NoChanges => "–",
		Health.Passed => "✓",
		Health.NeedsValidation => "!",
		Health.Failed => "✕",
		Health.Error => "?",
		_ => "",
	};

	static readonly Dictionary<Health, Icon> Icons = new();

	/// <summary>트레이/창 아이콘 (상태 색 원)</summary>
	public static Icon IconOf(Health health)
	{
		if (!Icons.TryGetValue(health, out Icon? icon))
		{
			using Bitmap bitmap = new Bitmap(32, 32);
			using (Graphics graphics = Graphics.FromImage(bitmap))
			{
				graphics.SmoothingMode = SmoothingMode.AntiAlias;
				using SolidBrush brush = new SolidBrush(ColorOf(health));
				graphics.FillEllipse(brush, 2, 2, 28, 28);
				using Pen pen = new Pen(Color.White, 3f);
				graphics.DrawEllipse(pen, 9, 9, 14, 14);
			}
			icon = Icon.FromHandle(bitmap.GetHicon());
			Icons[health] = icon;
		}
		return icon;
	}
}

/// <summary>
/// 상태 배지 (UGS 상태 패널의 큰 아이콘 역할)
/// </summary>
sealed class StatusBadge : Control
{
	Health _health = Health.Unknown;

	public Health Health
	{
		get => _health;
		set { _health = value; Invalidate(); }
	}

	public StatusBadge()
	{
		SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
		BackColor = Color.Transparent;
	}

	protected override void OnPaint(PaintEventArgs e)
	{
		e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
		int size = Math.Min(Width, Height) - 4;
		Rectangle bounds = new Rectangle((Width - size) / 2, (Height - size) / 2, size, size);
		using SolidBrush brush = new SolidBrush(HealthStyle.ColorOf(_health));
		e.Graphics.FillEllipse(brush, bounds);
		using Font font = new Font(Font.FontFamily, size * 0.42f, FontStyle.Bold, GraphicsUnit.Pixel);
		TextRenderer.DrawText(e.Graphics, HealthStyle.GlyphOf(_health), font, bounds, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
	}
}

/// <summary>
/// 빌드 로그 뷰. 작업 스레드에서 AppendLine 해도 되며 100ms 마다 묶어서 화면에 반영한다.
/// 오류/경고 줄은 색으로 구분하고, 더블클릭하면 LineActivated 가 발생한다.
/// </summary>
sealed class LogView : ListBox
{
	const int MaxLines = 60000;
	const int TrimTo = 50000;

	readonly ConcurrentQueue<string> _pending = new();
	readonly System.Windows.Forms.Timer _flushTimer;
	int _maxLineLength;

	public event Action<string>? LineActivated;

	public LogView()
	{
		DrawMode = DrawMode.OwnerDrawFixed;
		SelectionMode = SelectionMode.MultiExtended;
		IntegralHeight = false;
		HorizontalScrollbar = true;
		BorderStyle = BorderStyle.None;
		Font = new Font("Consolas", 9.5f);
		ItemHeight = Font.Height + 2;
		_flushTimer = new System.Windows.Forms.Timer { Interval = 100 };
		_flushTimer.Tick += (_, _) => Flush();
		_flushTimer.Start();
	}

	public void AppendLine(string line) => _pending.Enqueue(line);

	public void ClearLog()
	{
		while (_pending.TryDequeue(out _))
		{
		}
		Items.Clear();
		_maxLineLength = 0;
		HorizontalExtent = 0;
	}

	public void SetLines(IEnumerable<string> lines)
	{
		ClearLog();
		foreach (string line in lines)
		{
			_pending.Enqueue(line);
		}
		Flush();
	}

	void Flush()
	{
		if (_pending.IsEmpty)
		{
			return;
		}
		int visibleCount = Math.Max(1, ClientSize.Height / ItemHeight);
		bool followTail = Items.Count == 0 || TopIndex + visibleCount >= Items.Count - 1;

		List<string> lines = new List<string>();
		while (_pending.TryDequeue(out string? line))
		{
			lines.Add(line);
			_maxLineLength = Math.Max(_maxLineLength, line.Length);
		}

		BeginUpdate();
		try
		{
			if (Items.Count + lines.Count > MaxLines)
			{
				// 오래된 줄 정리 (RemoveAt 반복은 느리므로 다시 채운다)
				List<object> keep = Items.Cast<object>().Concat(lines).TakeLast(TrimTo).ToList();
				Items.Clear();
				Items.AddRange(keep.ToArray());
			}
			else
			{
				Items.AddRange(lines.Cast<object>().ToArray());
			}
			HorizontalExtent = TextRenderer.MeasureText(new string('W', Math.Min(_maxLineLength, 400)), Font).Width;
		}
		finally
		{
			EndUpdate();
		}
		if (followTail && Items.Count > 0)
		{
			TopIndex = Math.Max(0, Items.Count - visibleCount);
		}
	}

	static Color LineColor(string line)
	{
		// 검증 엔진과 같은 기준 (BuildOutputParser)
		if (BuildOutputParser.IsErrorLine(line))
		{
			return Color.FromArgb(0xD0, 0x30, 0x30);
		}
		if (line.Contains(": warning", StringComparison.OrdinalIgnoreCase) || line.StartsWith("Warning:", StringComparison.OrdinalIgnoreCase))
		{
			return Color.FromArgb(0xB0, 0x7A, 0x00);
		}
		if (line.StartsWith("[ShipCheck]", StringComparison.Ordinal) || line.StartsWith("ShipCheck:", StringComparison.Ordinal))
		{
			return line.Contains("통과") ? Color.FromArgb(0x1E, 0x8E, 0x4E) : Color.FromArgb(0x2F, 0x6F, 0xC0);
		}
		return SystemColors.WindowText;
	}

	protected override void OnDrawItem(DrawItemEventArgs e)
	{
		if (e.Index < 0 || e.Index >= Items.Count)
		{
			return;
		}
		e.DrawBackground();
		string text = Items[e.Index]?.ToString() ?? "";
		bool selected = (e.State & DrawItemState.Selected) != 0;
		Color color = selected ? SystemColors.HighlightText : LineColor(text);
		TextRenderer.DrawText(e.Graphics, text, Font, new Point(e.Bounds.X + 2, e.Bounds.Y + 1), color, TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
	}

	protected override void OnKeyDown(KeyEventArgs e)
	{
		if (e.Control && e.KeyCode == Keys.C)
		{
			CopySelection();
			e.Handled = true;
			return;
		}
		if (e.Control && e.KeyCode == Keys.A)
		{
			BeginUpdate();
			for (int i = 0; i < Items.Count; i++)
			{
				SetSelected(i, true);
			}
			EndUpdate();
			e.Handled = true;
			return;
		}
		base.OnKeyDown(e);
	}

	public void CopySelection()
	{
		StringBuilder builder = new StringBuilder();
		foreach (object item in SelectedItems)
		{
			builder.AppendLine(item.ToString());
		}
		if (builder.Length > 0)
		{
			Clipboard.SetText(builder.ToString());
		}
	}

	protected override void OnDoubleClick(EventArgs e)
	{
		base.OnDoubleClick(e);
		if (SelectedItem is string line)
		{
			LineActivated?.Invoke(line);
		}
	}

	protected override void Dispose(bool disposing)
	{
		if (disposing)
		{
			_flushTimer.Dispose();
		}
		base.Dispose(disposing);
	}
}

/// <summary>
/// 깜빡임 없는 ListView
/// </summary>
sealed class BufferedListView : ListView
{
	public BufferedListView()
	{
		DoubleBuffered = true;
		View = View.Details;
		FullRowSelect = true;
		HideSelection = false;
		BorderStyle = BorderStyle.None;
	}
}
