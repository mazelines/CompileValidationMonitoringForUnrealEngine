using ShipCheck.Core;
using ShipCheck.Infrastructure;

namespace ShipCheckMonitor;

/// <summary>
/// ShipCheck Monitor 메인 창.
///  - 상단: 프로젝트 선택 / 검증 / SVN 업데이트·커밋 툴바, 상태 패널 (UGS 스타일)
///  - 중앙: 변경 사항 / 오류 / 기록 탭
///  - 하단: 실시간 빌드 로그
///  - 백그라운드: 소스 변경 감시, 유휴 시간 자동 검증, 트레이 알림
/// </summary>
sealed class MainForm : Form
{
	readonly MonitorEngine _engine;
	readonly AppSettings _settings;
	readonly bool _startMinimized;
	readonly RegisteredWaitHandle _showWaitHandle;

	// 툴바
	ToolStripComboBox _projectCombo = null!;
	ToolStripButton _addProjectButton = null!;
	ToolStripButton _removeProjectButton = null!;
	ToolStripSplitButton _validateButton = null!;
	ToolStripButton _stopButton = null!;
	ToolStripButton _refreshButton = null!;
	ToolStripButton _updateButton = null!;
	ToolStripButton _commitButton = null!;
	ToolStripButton _settingsButton = null!;
	ToolStripDropDownButton _toolsButton = null!;
	ToolStripMenuItem _primeMenuItem = null!;

	// 상태 패널
	StatusBadge _badge = null!;
	Label _titleLabel = null!;
	Label _subtitleLabel = null!;
	Label _projectValue = null!;
	Label _engineValue = null!;
	Label _targetsValue = null!;
	Label _svnValue = null!;
	LinkLabel _hookLink = null!;
	LinkLabel _autoLink = null!;
	Panel _statusPanel = null!;
	readonly ToolTip _toolTip = new();
	TableLayoutPanel _info = null!;

	// 본문
	SplitContainer _split = null!;
	TabControl _tabs = null!;
	TabPage _changesPage = null!;
	TabPage _errorsPage = null!;
	TabPage _historyPage = null!;
	BufferedListView _changesList = null!;
	BufferedListView _errorsList = null!;
	BufferedListView _historyList = null!;
	LogView _log = null!;
	Label _logTitle = null!;
	ToolStripStatusLabel _statusText = null!;
	ToolStripStatusLabel _lastCheckText = null!;

	// 트레이
	NotifyIcon _tray = null!;
	ToolStripMenuItem _trayAutoItem = null!;
	ToolStripMenuItem _trayValidateItem = null!;

	// 타이머
	readonly System.Windows.Forms.Timer _refreshDebounce = new() { Interval = 1500 };
	readonly System.Windows.Forms.Timer _periodicTimer = new() { Interval = 60_000 };
	readonly System.Windows.Forms.Timer _autoTimer = new() { Interval = 5_000 };
	readonly System.Windows.Forms.Timer _clockTimer = new() { Interval = 1_000 };

	// 상태
	string? _project;
	QueryResult? _query;
	QueryResult? _renderedQuery;
	string? _queryError;
	int _queryGeneration;
	bool _queryRunning;
	bool _queryAgain;
	CancellationTokenSource? _queryCancel;
	DateTime _lastQueryTime;

	List<RunResult> _history = new();
	(DateTime, long) _historyStamp;
	RunResult? _shownRun;

	ValidationRun? _run;
	DateTime _lastCodeChangeUtc = DateTime.UtcNow;
	string? _autoAttemptedKey;
	bool _autoChecking;

	readonly List<FileSystemWatcher> _watchers = new();
	bool _allowShow;
	bool _exiting;
	bool _trayHintShown;
	Health _health = Health.Unknown;

	public MainForm(MonitorEngine engine,AppSettings settings, EventWaitHandle showEvent, string? initialProject, bool startMinimized)
	{
		_engine = engine;
		_settings = settings;
		_startMinimized = startMinimized;
		_allowShow = !startMinimized;

		BuildUi();
		RestoreWindowBounds();

		// 두 번째 실행 시 기존 창 표시
		_showWaitHandle = ThreadPool.RegisterWaitForSingleObject(showEvent, (_, _) =>
		{
			if (IsHandleCreated)
			{
				BeginInvoke(ShowFromTray);
			}
		}, null, -1, false);

		_refreshDebounce.Tick += (_, _) => { _refreshDebounce.Stop(); RequestRefresh(); };
		_periodicTimer.Tick += (_, _) => { if (_run == null) { RequestRefresh(); } };
		_autoTimer.Tick += async (_, _) => await TryAutoValidateAsync();
		_clockTimer.Tick += (_, _) => { if (_run != null) { UpdateStatusPanel(); } };
		_periodicTimer.Start();
		_autoTimer.Start();
		_clockTimer.Start();

		LoadProjects(initialProject);
	}

	#region UI 구성

	void BuildUi()
	{
		Text = "ShipCheck Monitor";
		Icon = HealthStyle.IconOf(Health.Unknown);
		MinimumSize = new Size(900, 600);
		AllowDrop = true;
		DragEnter += (_, e) => e.Effect = GetDroppedProject(e) != null ? DragDropEffects.Copy : DragDropEffects.None;
		DragDrop += (_, e) => { string? path = GetDroppedProject(e); if (path != null) { AddProject(path); } };

		// --- 툴바 ---
		ToolStrip toolbar = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, Padding = new Padding(6, 4, 6, 4), RenderMode = ToolStripRenderMode.System };
		_projectCombo = new ToolStripComboBox { DropDownStyle = ComboBoxStyle.DropDownList, AutoSize = false, Width = 380, ToolTipText = "프로젝트" };
		_projectCombo.ComboBox.DrawMode = DrawMode.Normal;
		_projectCombo.SelectedIndexChanged += (_, _) => { if (_projectCombo.SelectedItem is ProjectItem item) { SelectProject(item.Path); } };
		_addProjectButton = new ToolStripButton("＋", null, (_, _) => BrowseProject()) { ToolTipText = "프로젝트 추가 (.uproject)" };
		_removeProjectButton = new ToolStripButton("－", null, (_, _) => RemoveCurrentProject()) { ToolTipText = "목록에서 프로젝트 제거" };

		_validateButton = new ToolStripSplitButton("▶ 검증") { ToolTipText = "변경된 코드로 게임 타깃을 증분 빌드하여 검증" };
		_validateButton.ButtonClick += (_, _) => _ = StartValidationAsync("gui", all: false, force: false);
		_validateButton.DropDownItems.Add("검증 (통과 캐시 무시)", null, (_, _) => _ = StartValidationAsync("gui", all: false, force: true));
		_primeMenuItem = new ToolStripMenuItem("전체 빌드 (최초 1회 프라이밍)", null, (_, _) => StartPrime());
		_validateButton.DropDownItems.Add(_primeMenuItem);
		_stopButton = new ToolStripButton("■ 중지", null, (_, _) => StopRun()) { ToolTipText = "검증 중지 (UBT/컴파일러 포함)" };
		_refreshButton = new ToolStripButton("⟳ 새로고침", null, (_, _) => RequestRefresh()) { ToolTipText = "변경 사항과 검증 상태 다시 확인" };
		_updateButton = new ToolStripButton("⇩ 업데이트", null, async (_, _) => await RunTortoiseAsync("update")) { ToolTipText = "TortoiseSVN 업데이트" };
		_commitButton = new ToolStripButton("⇧ 커밋", null, async (_, _) => await CommitAsync()) { ToolTipText = "TortoiseSVN 커밋 (Pre-commit 훅이 검증 결과를 확인)" };
		_settingsButton = new ToolStripButton("⚙ 설정", null, (_, _) => OpenSettings());
		_settingsButton.Alignment = ToolStripItemAlignment.Right;

		_toolsButton = new ToolStripDropDownButton("도구") { Alignment = ToolStripItemAlignment.Right };
		_toolsButton.DropDownItems.Add("로그 폴더 열기", null, (_, _) => OpenProjectSubfolder(Path.Combine("Saved", "ShipCheck", "Logs")));
		_toolsButton.DropDownItems.Add("프로젝트 폴더 열기", null, (_, _) => OpenProjectSubfolder(""));
		_toolsButton.DropDownItems.Add("설정 파일 위치 열기", null, (_, _) => { if (_query != null) { Integrations.ShowInExplorer(_query.ConfigPath); } });
		_toolsButton.DropDownItems.Add(new ToolStripSeparator());
		_toolsButton.DropDownItems.Add("커밋 훅 명령 복사", null, (_, _) => CopyHookCommand());
		_toolsButton.DropDownItems.Add("TortoiseSVN 설정 열기", null, async (_, _) => await RunTortoiseAsync("settings", withPath: false));
		_toolsButton.DropDownItems.Add("SVN 로그 보기", null, async (_, _) => await RunTortoiseAsync("log"));
		_toolsButton.DropDownItems.Add(new ToolStripSeparator());
		_toolsButton.DropDownItems.Add("통과 캐시 초기화", null, (_, _) => ClearPassCache());

		toolbar.Items.AddRange(new ToolStripItem[]
		{
			new ToolStripLabel("프로젝트"), _projectCombo, _addProjectButton, _removeProjectButton, new ToolStripSeparator(),
			_validateButton, _stopButton, _refreshButton, new ToolStripSeparator(),
			_updateButton, _commitButton,
			_settingsButton, _toolsButton,
		});

		// --- 상태 패널 ---
		Panel statusPanel = _statusPanel = new Panel { Dock = DockStyle.Top, Height = 150, Padding = new Padding(16, 12, 16, 10), BackColor = SystemColors.Window };
		statusPanel.Paint += (_, e) => e.Graphics.DrawLine(SystemPens.ControlLight, 0, statusPanel.Height - 1, statusPanel.Width, statusPanel.Height - 1);
		_badge = new StatusBadge { Dock = DockStyle.Left, Width = 88 };
		TableLayoutPanel info = _info = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 5, Padding = new Padding(14, 0, 0, 0) };
		info.Resize += (_, _) => LayoutStatusPanel();
		info.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
		info.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
		info.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
		info.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
		_titleLabel = new Label { AutoSize = true, Font = new Font(Font.FontFamily, 15f, FontStyle.Bold), Margin = new Padding(0, 0, 0, 2) };
		_subtitleLabel = new Label { AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(0, 0, 0, 8) };
		info.Controls.Add(_titleLabel, 0, 0);
		info.SetColumnSpan(_titleLabel, 4);
		info.Controls.Add(_subtitleLabel, 0, 1);
		info.SetColumnSpan(_subtitleLabel, 4);
		_projectValue = ValueLabel();
		_engineValue = ValueLabel();
		_targetsValue = ValueLabel();
		_svnValue = ValueLabel();
		_hookLink = new LinkLabel { AutoSize = true, Margin = new Padding(0, 2, 12, 2) };
		_hookLink.LinkClicked += (_, _) => CopyHookCommand();
		_autoLink = new LinkLabel { AutoSize = true, Margin = new Padding(0, 2, 12, 2) };
		_autoLink.LinkClicked += (_, _) => SetAutoValidate(!_settings.AutoValidate);
		AddInfoRow(info, 2, "프로젝트", _projectValue, "엔진", _engineValue);
		AddInfoRow(info, 3, "타깃", _targetsValue, "SVN", _svnValue);
		AddInfoRow(info, 4, "커밋 훅", _hookLink, "자동 검증", _autoLink);
		statusPanel.Controls.Add(info);
		statusPanel.Controls.Add(_badge);

		// --- 탭 ---
		_changesList = new BufferedListView();
		_changesList.Columns.Add("상태", 70);
		_changesList.Columns.Add("파일", 460);
		_changesList.Columns.Add("모듈", 150);
		_changesList.Columns.Add("모듈 타입", 120);
		_changesList.Columns.Add("빌드 영향", 90);
		_changesList.DoubleClick += (_, _) => OpenSelectedChange();
		_changesList.ContextMenuStrip = BuildChangesMenu();

		_errorsList = new BufferedListView();
		_errorsList.Columns.Add("파일", 260);
		_errorsList.Columns.Add("줄", 55, HorizontalAlignment.Right);
		_errorsList.Columns.Add("코드", 75);
		_errorsList.Columns.Add("메시지", 560);
		_errorsList.Columns.Add("단계", 220);
		_errorsList.DoubleClick += (_, _) => OpenSelectedError();
		ContextMenuStrip errorsMenu = new ContextMenuStrip();
		errorsMenu.Items.Add("편집기에서 열기", null, (_, _) => OpenSelectedError());
		errorsMenu.Items.Add("오류 복사", null, (_, _) => CopySelected(_errorsList, x => ((Diagnostic)x.Tag!).Detail ?? ((Diagnostic)x.Tag!).Message));
		_errorsList.ContextMenuStrip = errorsMenu;

		_historyList = new BufferedListView();
		_historyList.Columns.Add("시간", 140);
		_historyList.Columns.Add("결과", 85);
		_historyList.Columns.Add("실행", 75);
		_historyList.Columns.Add("소요", 70, HorizontalAlignment.Right);
		_historyList.Columns.Add("파일", 55, HorizontalAlignment.Right);
		_historyList.Columns.Add("리비전", 65, HorizontalAlignment.Right);
		_historyList.Columns.Add("내용", 520);
		_historyList.DoubleClick += (_, _) => { if (_historyList.SelectedItems.Count > 0 && _historyList.SelectedItems[0].Tag is RunResult run) { ShowRun(run, loadLogs: true); } };

		_tabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point(14, 5) };
		_changesPage = new TabPage("변경 사항");
		_errorsPage = new TabPage("오류");
		_historyPage = new TabPage("기록");
		_changesPage.Controls.Add(_changesList);
		_errorsPage.Controls.Add(_errorsList);
		_historyPage.Controls.Add(_historyList);
		_changesList.Dock = _errorsList.Dock = _historyList.Dock = DockStyle.Fill;
		_tabs.TabPages.AddRange(new[] { _changesPage, _errorsPage, _historyPage });

		// --- 로그 ---
		_log = new LogView { Dock = DockStyle.Fill };
		_log.LineActivated += line => OpenError(BuildOutputParser.Parse(line, ""));
		ContextMenuStrip logMenu = new ContextMenuStrip();
		logMenu.Items.Add("복사", null, (_, _) => _log.CopySelection());
		logMenu.Items.Add("지우기", null, (_, _) => _log.ClearLog());
		_log.ContextMenuStrip = logMenu;
		_logTitle = new Label { Dock = DockStyle.Top, Height = 26, Text = "빌드 로그", TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(6, 0, 0, 0), Font = new Font(Font, FontStyle.Bold), BackColor = SystemColors.Control };

		_split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterWidth = 6, BackColor = SystemColors.Control };
		_split.Panel1.Controls.Add(_tabs);
		_split.Panel2.Controls.Add(_log);
		_split.Panel2.Controls.Add(_logTitle);

		// --- 상태 표시줄 ---
		StatusStrip statusStrip = new StatusStrip();
		_statusText = new ToolStripStatusLabel { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
		_lastCheckText = new ToolStripStatusLabel();
		statusStrip.Items.AddRange(new ToolStripItem[] { _statusText, _lastCheckText });

		// Dock 순서: 나중에 추가한 컨트롤이 바깥쪽에 배치된다
		Controls.Add(_split);
		Controls.Add(statusPanel);
		Controls.Add(toolbar);
		Controls.Add(statusStrip);

		// --- 트레이 ---
		ContextMenuStrip trayMenu = new ContextMenuStrip();
		trayMenu.Items.Add("열기", null, (_, _) => ShowFromTray());
		_trayValidateItem = new ToolStripMenuItem("지금 검증", null, (_, _) => _ = StartValidationAsync("gui", all: false, force: false));
		trayMenu.Items.Add(_trayValidateItem);
		_trayAutoItem = new ToolStripMenuItem("자동 검증", null, (_, _) => SetAutoValidate(!_settings.AutoValidate));
		trayMenu.Items.Add(_trayAutoItem);
		trayMenu.Items.Add(new ToolStripSeparator());
		trayMenu.Items.Add("종료", null, (_, _) => ExitApplication());
		_tray = new NotifyIcon { Icon = HealthStyle.IconOf(Health.Unknown), Text = "ShipCheck Monitor", Visible = true, ContextMenuStrip = trayMenu };
		_tray.DoubleClick += (_, _) => ShowFromTray();
		_tray.BalloonTipClicked += (_, _) => { ShowFromTray(); if (_errorsList.Items.Count > 0) { _tabs.SelectedTab = _errorsPage; } };
	}

	// 긴 경로는 칸 너비에 맞춰 … 으로 줄인다 (전체 경로는 툴팁)
	static Label ValueLabel() => new Label { AutoSize = false, AutoEllipsis = true, Dock = DockStyle.Fill, Height = 20, Margin = new Padding(0, 2, 12, 2) };

	/// <summary>상태 패널 높이를 내용(줄바꿈된 부제목 포함)에 맞춘다</summary>
	void LayoutStatusPanel()
	{
		if (_info.Width <= 0)
		{
			return;
		}
		int textWidth = Math.Max(200, _info.ClientSize.Width - _info.Padding.Horizontal - 8);
		_titleLabel.MaximumSize = new Size(textWidth, 0);
		_subtitleLabel.MaximumSize = new Size(textWidth, 0);
		int needed = _info.GetPreferredSize(new Size(_info.Width, 0)).Height + _statusPanel.Padding.Vertical;
		int height = Math.Clamp(needed, 120, 260);
		if (_statusPanel.Height != height)
		{
			_statusPanel.Height = height;
		}
	}

	static void AddInfoRow(TableLayoutPanel table, int row, string key1, Control value1, string key2, Control value2)
	{
		table.Controls.Add(KeyLabel(key1), 0, row);
		table.Controls.Add(value1, 1, row);
		table.Controls.Add(KeyLabel(key2), 2, row);
		table.Controls.Add(value2, 3, row);
	}

	static Label KeyLabel(string text) => new Label { Text = text, AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(0, 2, 10, 2) };

	ContextMenuStrip BuildChangesMenu()
	{
		ContextMenuStrip menu = new ContextMenuStrip();
		menu.Items.Add("편집기에서 열기", null, (_, _) => OpenSelectedChange());
		menu.Items.Add("차이 보기 (TortoiseSVN)", null, async (_, _) =>
		{
			if (SelectedChange() is ChangeEntry change)
			{
				await RunTortoiseAsync("diff", change.Path);
			}
		});
		menu.Items.Add("탐색기에서 보기", null, (_, _) => { if (SelectedChange() is ChangeEntry change) { Integrations.ShowInExplorer(change.Path); } });
		menu.Items.Add("경로 복사", null, (_, _) => CopySelected(_changesList, x => ((ChangeEntry)x.Tag!).Path));
		return menu;
	}

	void RestoreWindowBounds()
	{
		StartPosition = FormStartPosition.Manual;
		Rectangle bounds = new Rectangle(_settings.WindowX, _settings.WindowY, _settings.WindowWidth, _settings.WindowHeight);
		if (_settings.WindowX < 0 || !Screen.AllScreens.Any(x => x.WorkingArea.IntersectsWith(bounds)))
		{
			Rectangle area = Screen.PrimaryScreen!.WorkingArea;
			bounds = new Rectangle(area.X + (area.Width - _settings.WindowWidth) / 2, area.Y + (area.Height - _settings.WindowHeight) / 2, _settings.WindowWidth, _settings.WindowHeight);
		}
		Bounds = bounds;
		if (_settings.WindowMaximized)
		{
			WindowState = FormWindowState.Maximized;
		}
	}

	protected override void OnShown(EventArgs e)
	{
		base.OnShown(e);
		_split.SplitterDistance = Math.Clamp(_settings.SplitterDistance, 120, Math.Max(120, _split.Height - 120));
	}

	protected override void SetVisibleCore(bool value)
	{
		// --minimized 로 시작하면 트레이에만 표시
		if (!_allowShow)
		{
			value = false;
			if (!IsHandleCreated)
			{
				CreateHandle();
			}
		}
		base.SetVisibleCore(value);
	}

	#endregion

	#region 프로젝트

	sealed class ProjectItem
	{
		public string Path { get; }
		public ProjectItem(string path) => Path = path;
		public override string ToString() => $"{System.IO.Path.GetFileNameWithoutExtension(Path)}    —  {System.IO.Path.GetDirectoryName(Path)}";
	}

	void LoadProjects(string? initialProject)
	{
		_settings.Projects = _settings.Projects.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
		RefreshProjectCombo();
		if (initialProject != null)
		{
			AddProject(initialProject);
		}
		else
		{
			string? select = _settings.Projects.FirstOrDefault(x => x.Equals(_settings.SelectedProject, StringComparison.OrdinalIgnoreCase)) ?? _settings.Projects.FirstOrDefault();
			if (select != null)
			{
				SelectComboItem(select);
			}
			else
			{
				UpdateAll();
			}
		}
	}

	void RefreshProjectCombo()
	{
		_projectCombo.Items.Clear();
		foreach (string project in _settings.Projects)
		{
			_projectCombo.Items.Add(new ProjectItem(project));
		}
	}

	void SelectComboItem(string project)
	{
		foreach (ProjectItem item in _projectCombo.Items)
		{
			if (item.Path.Equals(project, StringComparison.OrdinalIgnoreCase))
			{
				_projectCombo.SelectedItem = item;
				return;
			}
		}
	}

	static string? ResolveUProject(string path)
	{
		if (Directory.Exists(path))
		{
			return Directory.GetFiles(path, "*.uproject").FirstOrDefault();
		}
		return File.Exists(path) && path.EndsWith(".uproject", StringComparison.OrdinalIgnoreCase) ? Path.GetFullPath(path) : null;
	}

	static string? GetDroppedProject(DragEventArgs e)
	{
		return e.Data?.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files ? ResolveUProject(files[0]) : null;
	}

	void BrowseProject()
	{
		using OpenFileDialog dialog = new OpenFileDialog { Filter = "Unreal 프로젝트 (*.uproject)|*.uproject", Title = "검사할 프로젝트 선택" };
		if (dialog.ShowDialog(this) == DialogResult.OK)
		{
			AddProject(dialog.FileName);
		}
	}

	void AddProject(string path)
	{
		string? uproject = ResolveUProject(path);
		if (uproject == null)
		{
			MessageBox.Show(this, $".uproject 파일을 찾을 수 없습니다:\n{path}", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
			return;
		}
		if (_run != null)
		{
			MessageBox.Show(this, "검증 중에는 프로젝트를 바꿀 수 없습니다.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
			return;
		}
		if (!_settings.Projects.Contains(uproject, StringComparer.OrdinalIgnoreCase))
		{
			_settings.Projects.Add(uproject);
			_settings.Save();
			RefreshProjectCombo();
		}
		SelectComboItem(uproject);
	}

	void RemoveCurrentProject()
	{
		if (_project == null || _run != null)
		{
			return;
		}
		if (MessageBox.Show(this, $"목록에서 제거할까요? (파일은 삭제되지 않습니다)\n{_project}", Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
		{
			return;
		}
		_settings.Projects.RemoveAll(x => x.Equals(_project, StringComparison.OrdinalIgnoreCase));
		_settings.SelectedProject = null;
		_settings.Save();
		_project = null;
		ResetProjectState();
		RefreshProjectCombo();
		if (_settings.Projects.Count > 0)
		{
			SelectComboItem(_settings.Projects[0]);
		}
		else
		{
			UpdateAll();
		}
	}

	void SelectProject(string project)
	{
		if (project.Equals(_project, StringComparison.OrdinalIgnoreCase))
		{
			return;
		}
		if (_run != null)
		{
			// 실행 중에는 콤보가 비활성화되지만 혹시 모를 경우 원래 선택으로 되돌린다
			SelectComboItem(_run.Project);
			return;
		}
		_project = project;
		_settings.SelectedProject = project;
		_settings.Save();
		ResetProjectState();
		_history = MonitorEngine.LoadHistory(ProjectDir!);
		_historyStamp = GetHistoryStamp();
		_shownRun = _history.FirstOrDefault(x => x.Steps is { Count: > 0 });
		RenderHistory();
		RenderErrors();
		CreateWatchers();
		RequestRefresh();
	}

	void ResetProjectState()
	{
		// 이전 프로젝트의 조회 결과가 늦게 도착해도 무시되도록 세대 번호를 올린다
		_queryGeneration++;
		_queryCancel?.Cancel();
		_queryRunning = false;
		_queryAgain = false;
		_query = null;
		_renderedQuery = null;
		_queryError = null;
		_history = new List<RunResult>();
		_shownRun = null;
		_autoAttemptedKey = null;
		_lastCodeChangeUtc = DateTime.UtcNow;
		_changesList.Items.Clear();
		_errorsList.Items.Clear();
		_historyList.Items.Clear();
		_log.ClearLog();
		DisposeWatchers();
		UpdateAll();
	}

	string? ProjectDir => _project == null ? null : Path.GetDirectoryName(_project);

	#endregion

	#region 변경 감시 / 상태 조회

	void CreateWatchers()
	{
		DisposeWatchers();
		string? projectDir = ProjectDir;
		if (projectDir == null)
		{
			return;
		}
		foreach (string sub in new[] { "Source", "Plugins" })
		{
			string dir = Path.Combine(projectDir, sub);
			if (Directory.Exists(dir))
			{
				AddWatcher(dir, "*.*", recursive: true);
			}
		}
		AddWatcher(projectDir, "*.uproject", recursive: false);
	}

	void AddWatcher(string dir, string filter, bool recursive)
	{
		FileSystemWatcher watcher = new FileSystemWatcher(dir, filter)
		{
			IncludeSubdirectories = recursive,
			NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size,
			InternalBufferSize = 64 * 1024,
		};
		FileSystemEventHandler onChange = (_, e) => OnFileEvent(e.FullPath);
		watcher.Changed += onChange;
		watcher.Created += onChange;
		watcher.Deleted += onChange;
		watcher.Renamed += (_, e) => OnFileEvent(e.FullPath);
		watcher.Error += (_, _) => OnFileEvent(null);
		watcher.EnableRaisingEvents = true;
		_watchers.Add(watcher);
	}

	static readonly string[] CodeExtensions = { ".cpp", ".h", ".hpp", ".inl", ".c", ".cc", ".cxx", ".ipp", ".ispc", ".cs", ".uproject", ".uplugin" };

	static bool IsCodePath(string path)
	{
		if (path.Contains(@"\Intermediate\", StringComparison.OrdinalIgnoreCase) || path.Contains(@"\Binaries\", StringComparison.OrdinalIgnoreCase) ||
			path.Contains(@"\Saved\", StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}
		string extension = Path.GetExtension(path);
		return CodeExtensions.Any(x => x.Equals(extension, StringComparison.OrdinalIgnoreCase)) || !Path.HasExtension(path);
	}

	// FileSystemWatcher 스레드에서 호출됨
	void OnFileEvent(string? path)
	{
		if (path != null && !IsCodePath(path))
		{
			return;
		}
		if (IsHandleCreated)
		{
			BeginInvoke(() =>
			{
				_lastCodeChangeUtc = DateTime.UtcNow;
				_refreshDebounce.Stop();
				_refreshDebounce.Start();
			});
		}
	}

	void DisposeWatchers()
	{
		foreach (FileSystemWatcher watcher in _watchers)
		{
			watcher.EnableRaisingEvents = false;
			watcher.Dispose();
		}
		_watchers.Clear();
	}

	void RequestRefresh()
	{
		if (_project == null)
		{
			return;
		}
		if (_queryRunning)
		{
			_queryAgain = true;
			return;
		}
		_ = RunQueryAsync();
	}

	async Task RunQueryAsync()
	{
		string project = _project!;
		int generation = ++_queryGeneration;
		_queryRunning = true;
		_queryCancel = new CancellationTokenSource();
		CancellationToken token = _queryCancel.Token;
		UpdateToolbar();
		_statusText.Text = "상태 확인 중...";
		try
		{
			QueryResult result = await _engine.QueryAsync(project, token);
			if (generation != _queryGeneration || project != _project)
			{
				return;
			}
			_query = result;
			_queryError = null;
		}
		catch (OperationCanceledException)
		{
			return;
		}
		catch (Exception ex)
		{
			if (generation != _queryGeneration || project != _project)
			{
				return;
			}
			_queryError = ex.Message;
		}
		finally
		{
			if (generation == _queryGeneration)
			{
				_queryRunning = false;
				_lastQueryTime = DateTime.Now;
			}
		}

		ReloadHistoryIfChanged();
		UpdateAll();
		if (_queryAgain)
		{
			_queryAgain = false;
			RequestRefresh();
		}
	}

	(DateTime, long) GetHistoryStamp()
	{
		string? projectDir = ProjectDir;
		if (projectDir == null)
		{
			return default;
		}
		FileInfo file = new FileInfo(Path.Combine(projectDir, "Saved", "ShipCheck", "History.jsonl"));
		return file.Exists ? (file.LastWriteTimeUtc, file.Length) : default;
	}

	void ReloadHistoryIfChanged()
	{
		string? projectDir = ProjectDir;
		if (projectDir == null)
		{
			return;
		}
		(DateTime, long) stamp = GetHistoryStamp();
		if (stamp == _historyStamp)
		{
			return;
		}
		_historyStamp = stamp;
		_history = MonitorEngine.LoadHistory(projectDir);
		// 훅 등 다른 곳에서 새 실행이 기록되었고 보고 있는 결과가 없거나 더 오래되었다면 최신 결과로 바꾼다
		RunResult? latest = _history.FirstOrDefault(x => x.Steps is { Count: > 0 });
		if (latest != null && (_shownRun == null || latest.Time > _shownRun.Time) && _run == null)
		{
			_shownRun = latest;
			RenderErrors();
		}
		RenderHistory();
	}

	#endregion

	#region 상태 계산 / 화면 갱신

	// 상태 판정은 공통 로직 (캐시를 꺼도 상태 키로 실패를 찾고, SVN 상태를 모르면 "변경 없음" 으로 보지 않는다)
	Health ComputeHealth() => HealthEvaluator.Evaluate(_project != null, _run != null, _queryError, _query, _history);

	/// <summary>현재 워킹 카피 상태로 마지막에 실행한 검증이 실패였다면 그 기록</summary>
	RunResult? LatestFailureForCurrentState() => HealthEvaluator.LatestFailure(_query, _history);

	void UpdateAll()
	{
		_health = ComputeHealth();
		UpdateStatusPanel();
		UpdateToolbar();
		if (!ReferenceEquals(_renderedQuery, _query))
		{
			RenderChanges();
		}
		UpdateTray();
		_lastCheckText.Text = _lastQueryTime == default ? "" : $"마지막 확인 {_lastQueryTime:HH:mm:ss}";
		if (!_queryRunning)
		{
			_statusText.Text = _run != null ? "검증 중" : "";
		}
	}

	void UpdateStatusPanel()
	{
		_health = ComputeHealth();
		_badge.Health = _health;
		Icon = HealthStyle.IconOf(_health);

		int affecting = _query?.AffectingChanges.Count() ?? 0;
		RunResult? lastRun = _history.FirstOrDefault(x => x.Result is "Pass" or "Fail" or "Cached");
		string lastRunText = lastRun == null ? "" : $"마지막 검증 {lastRun.Time.LocalDateTime:MM-dd HH:mm} · {lastRun.TriggerText} · {lastRun.ResultText}";

		switch (_health)
		{
			case Health.Running:
				TimeSpan elapsed = DateTime.UtcNow - _run!.StartedUtc;
				_titleLabel.Text = $"{(_run.All ? "전체 빌드" : "검증")} 중...  {(int)elapsed.TotalMinutes}:{elapsed.Seconds:00}";
				_subtitleLabel.Text = _run.Trigger == "auto" ? "유휴 시간 자동 검증 · 중지하려면 ■ 중지" : "게임 타깃 증분 빌드 진행 중 · 아래 빌드 로그 참고";
				break;
			case Health.NoChanges:
				_titleLabel.Text = "검증할 코드 변경 없음";
				_subtitleLabel.Text = (_query!.Changes.Count > 0 ? $"에디터 전용 등 게임 빌드에 영향 없는 변경 {_query.Changes.Count}개 · " : "") + lastRunText;
				break;
			case Health.Passed:
				// 통과 범위는 "현재 워킹 카피의 빌드" 이다 (커밋에서 빠지는 파일까지 보장하지 않음)
				_titleLabel.Text = "검증 통과 — 현재 워킹 카피 기준";
				_subtitleLabel.Text = $"현재 변경 사항({affecting}개)이 게임 빌드 검증을 통과했습니다." +
					(CurrentHookStatus().BlocksCommits ? " 커밋 훅은 캐시로 즉시 통과합니다." : "") + $" · {lastRunText}";
				break;
			case Health.NeedsValidation when !_query!.SvnAvailable:
				_titleLabel.Text = "변경 사항 확인 불가 — SVN 워킹 카피 상태를 읽을 수 없음";
				_subtitleLabel.Text = "svn 명령줄 도구가 없거나 이 프로젝트가 SVN 워킹 카피가 아닙니다. ▶ 검증 ▸ 전체 빌드로 직접 확인하세요. " + lastRunText;
				break;
			case Health.NeedsValidation:
				_titleLabel.Text = $"검증 필요 — 빌드 영향 코드 변경 {affecting}개";
				_subtitleLabel.Text = _settings.AutoValidate
					? $"코드 변경이 {_settings.IdleSeconds}초 동안 없으면 자동으로 검증합니다. 바로 하려면 ▶ 검증."
					: CurrentHookStatus().BlocksCommits ? "▶ 검증을 누르거나, 커밋할 때 훅이 검증합니다." : "▶ 검증을 눌러 확인하세요. (커밋 훅 적용이 확인되지 않음)";
				break;
			case Health.Failed:
				RunResult failure = LatestFailureForCurrentState()!;
				_titleLabel.Text = "검증 실패 — 오류를 수정하세요";
				_subtitleLabel.Text = $"{failure.Time.LocalDateTime:MM-dd HH:mm} {failure.TriggerText} 검증 실패 · {failure.Reason} · 오류 탭에서 더블클릭하면 해당 줄로 이동";
				break;
			case Health.Error:
				_titleLabel.Text = "상태 확인 실패";
				_subtitleLabel.Text = FirstLine(_queryError) + "\n⚙ 설정 ▸ 프로젝트 탭에서 엔진 경로 등을 지정할 수 있습니다.";
				break;
			default:
				_titleLabel.Text = _project == null ? "프로젝트를 추가하세요" : "상태 확인 중...";
				_subtitleLabel.Text = _project == null ? "＋ 버튼으로 .uproject 를 선택하거나 창에 끌어다 놓으세요." : "";
				break;
		}
		_titleLabel.ForeColor = _health is Health.Failed or Health.Error ? HealthStyle.ColorOf(Health.Failed) : SystemColors.ControlText;

		// 프라이밍 / 미등록 소스 안내
		if (_query != null && _health is not Health.Running)
		{
			List<string> notes = new List<string>();
			if (_query.Targets.Any(x => !x.Primed))
			{
				notes.Add("⚠ 증분 빌드 캐시가 없습니다. 첫 검증은 전체 빌드라 오래 걸립니다 (▶ 검증 ▸ 전체 빌드).");
			}
			int unversioned = _query.Changes.Count(x => x.IsUnversioned && x.Affects);
			if (unversioned > 0)
			{
				notes.Add($"⚠ svn add 되지 않은 소스 {unversioned}개 — 커밋에서 빠지면 저장소 빌드가 깨집니다.");
			}
			if (notes.Count > 0)
			{
				_subtitleLabel.Text = string.Join("  ", notes) + (string.IsNullOrEmpty(_subtitleLabel.Text) ? "" : "\n" + _subtitleLabel.Text);
			}
		}

		_projectValue.Text = _project ?? "-";
		_engineValue.Text = _query?.EngineRoot ?? "-";
		_targetsValue.Text = _query == null ? "-" : string.Join(", ", _query.Targets.Select(x => x.Primed ? x.ToString() : $"{x} (빌드 캐시 없음)"));
		_svnValue.Text = _query == null ? "-"
			: !_query.SvnAvailable ? "svn 명령줄 도구 없음 또는 워킹 카피 아님 (캐시 비활성)"
			: $"r{_query.Revision} · 코드 변경 {_query.Changes.Count}개 (빌드 영향 {affecting}개)";

		HookStatus hook = CurrentHookStatus();
		_hookLink.Text = _project == null ? "-" : hook.DisplayText;
		_hookLink.LinkColor = hook.BlocksCommits ? HealthStyle.ColorOf(Health.Passed) : HealthStyle.ColorOf(Health.NeedsValidation);
		_autoLink.Text = _settings.AutoValidate ? $"켜짐 (유휴 {_settings.IdleSeconds}초)" : "꺼짐 — 클릭하여 켜기";
		_trayAutoItem.Checked = _settings.AutoValidate;

		foreach (Label label in new[] { _projectValue, _engineValue, _targetsValue, _svnValue })
		{
			if (_toolTip.GetToolTip(label) != label.Text)
			{
				_toolTip.SetToolTip(label, label.Text);
			}
		}
		LayoutStatusPanel();
	}

	HookStatus CurrentHookStatus() => _project == null
		? new HookStatus(HookState.NotRegistered, "")
		: _engine.HookStatus(_project);

	static string FirstLine(string? text) => text?.Split('\n').Select(x => x.Trim()).FirstOrDefault(x => x.Length > 0) ?? "";

	void UpdateToolbar()
	{
		bool hasProject = _project != null;
		bool running = _run != null;
		bool hasChanges = _query?.Changes.Count > 0;
		_projectCombo.Enabled = !running;
		_addProjectButton.Enabled = !running;
		_removeProjectButton.Enabled = hasProject && !running;
		_validateButton.Enabled = hasProject && !running && _query != null;
		_validateButton.Text = _health == Health.Passed ? "▶ 다시 검증" : "▶ 검증";
		_primeMenuItem.Enabled = hasProject && !running;
		_trayValidateItem.Enabled = hasProject && !running && hasChanges;
		_stopButton.Enabled = running;
		_refreshButton.Enabled = hasProject && !_queryRunning;
		bool tortoise = Integrations.FindTortoiseProc() != null;
		_updateButton.Enabled = hasProject && tortoise && !running;
		_commitButton.Enabled = hasProject && tortoise && !running;
	}

	void UpdateTray()
	{
		_tray.Icon = HealthStyle.IconOf(_health);
		string text = $"ShipCheck · {(_project == null ? "프로젝트 없음" : Path.GetFileNameWithoutExtension(_project))} · {_titleLabel.Text}";
		_tray.Text = text.Length > 120 ? text[..120] : text;
	}

	void RenderChanges()
	{
		_renderedQuery = _query;
		_changesList.BeginUpdate();
		_changesList.Items.Clear();
		string projectDir = ProjectDir ?? "";
		IEnumerable<ChangeEntry> changes = (_query?.Changes ?? new List<ChangeEntry>()).OrderByDescending(x => x.Affects).ThenBy(x => x.Path, StringComparer.OrdinalIgnoreCase);
		foreach (ChangeEntry change in changes)
		{
			ListViewItem item = new ListViewItem(change.StatusText) { Tag = change };
			item.SubItems.Add(Path.GetRelativePath(projectDir, change.Path));
			item.SubItems.Add(change.Module ?? "");
			item.SubItems.Add(string.IsNullOrEmpty(change.ModuleType) ? change.Kind : change.ModuleType);
			item.SubItems.Add(change.Affects ? "예" : "아니오");
			if (!change.Affects)
			{
				item.ForeColor = SystemColors.GrayText;
			}
			else if (change.IsUnversioned)
			{
				item.ForeColor = HealthStyle.ColorOf(Health.NeedsValidation);
			}
			_changesList.Items.Add(item);
		}
		_changesList.EndUpdate();
		int affecting = _query?.AffectingChanges.Count() ?? 0;
		_changesPage.Text = _query == null ? "변경 사항" : $"변경 사항 ({affecting}/{_query.Changes.Count})";
	}

	void RenderErrors()
	{
		_errorsList.BeginUpdate();
		_errorsList.Items.Clear();
		List<Diagnostic> errors = _shownRun == null ? new List<Diagnostic>() : BuildOutputParser.FromRun(_shownRun);
		foreach (Diagnostic error in errors)
		{
			ListViewItem item = new ListViewItem(error.File == null ? "" : Path.GetFileName(error.File)) { Tag = error, ToolTipText = error.File };
			item.SubItems.Add(error.Line is int line && line > 0 ? line.ToString() : "");
			item.SubItems.Add(error.ToolCode ?? (error.Code is DiagnosticCodes.CompileError or DiagnosticCodes.BuildFailed ? "" : error.Code));
			item.SubItems.Add(error.Message);
			item.SubItems.Add(error.Stage);
			_errorsList.Items.Add(item);
		}
		_errorsList.EndUpdate();
		_errorsPage.Text = errors.Count > 0 ? $"오류 ({errors.Count})" : "오류";
		_logTitle.Text = _shownRun == null ? "빌드 로그" : $"빌드 로그 — {_shownRun.Time.LocalDateTime:MM-dd HH:mm:ss} {_shownRun.TriggerText} {_shownRun.ResultText}";
	}

	void RenderHistory()
	{
		_historyList.BeginUpdate();
		_historyList.Items.Clear();
		foreach (RunResult run in _history)
		{
			ListViewItem item = new ListViewItem(run.Time.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss")) { Tag = run };
			item.SubItems.Add(run.ResultText);
			item.SubItems.Add(run.TriggerText);
			item.SubItems.Add(run.ElapsedSeconds >= 60 ? $"{(int)(run.ElapsedSeconds / 60)}m{(int)(run.ElapsedSeconds % 60):00}s" : $"{run.ElapsedSeconds:0.#}s");
			item.SubItems.Add((run.Files?.Count ?? 0).ToString());
			item.SubItems.Add(run.Revision ?? "");
			item.SubItems.Add(run.Reason ?? "");
			item.ForeColor = run.Result switch
			{
				"Fail" or "Error" => HealthStyle.ColorOf(Health.Failed),
				"Pass" or "Cached" => HealthStyle.ColorOf(Health.Passed),
				"Skipped" => HealthStyle.ColorOf(Health.NeedsValidation),
				_ => SystemColors.WindowText,
			};
			_historyList.Items.Add(item);
		}
		_historyList.EndUpdate();
	}

	void ShowRun(RunResult run, bool loadLogs)
	{
		_shownRun = run;
		RenderErrors();
		if (loadLogs)
		{
			List<string> lines = new List<string>();
			foreach (StepResult step in run.Steps ?? new List<StepResult>())
			{
				lines.Add($"===== {step.Label}  (종료코드 {step.ExitCode})  {step.Log}");
				if (step.Log != null && File.Exists(step.Log))
				{
					lines.AddRange(File.ReadAllLines(step.Log));
				}
				else
				{
					lines.Add("(로그 파일이 없습니다)");
					lines.AddRange(step.Errors ?? new List<string>());
				}
			}
			if (lines.Count == 0)
			{
				lines.Add($"{run.ResultText}: {run.Reason}");
			}
			_log.SetLines(lines);
		}
		if (_errorsList.Items.Count > 0)
		{
			_tabs.SelectedTab = _errorsPage;
		}
	}

	#endregion

	#region 검증 실행

	async Task StartValidationAsync(string trigger, bool all, bool force)
	{
		if (_project == null || _run != null)
		{
			return;
		}
		List<string> paths = _query?.Changes.Select(x => x.Path).ToList() ?? new List<string>();
		if (!all && paths.Count == 0)
		{
			if (trigger != "auto")
			{
				MessageBox.Show(this, "검증할 코드 변경이 없습니다.\n(전체 빌드는 ▶ 검증 옆 메뉴에서 실행할 수 있습니다)", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
			}
			return;
		}

		_log.ClearLog();
		_log.AppendLine($"[ShipCheck Monitor] {DateTime.Now:HH:mm:ss} {(all ? "전체 빌드" : "검증")} 시작 ({trigger})");
		_logTitle.Text = "빌드 로그 — 실행 중";
		_shownRun = null;
		RenderErrors();
		ValidationRun run = _engine.StartValidation(_project, paths, all, force, trigger, AppendEvent);
		_run = run;
		UpdateAll();

		RunResult result;
		try
		{
			result = await run.Completion;
		}
		catch (Exception ex)
		{
			// 엔진은 오류를 결과로 바꿔 돌려주므로 여기는 예상하지 못한 경우뿐
			result = new RunResult { Result = nameof(RunOutcome.Error), Reason = ex.Message, Trigger = trigger, StartTime = DateTimeOffset.Now.ToString("o"),
				Diagnostics = new() { Diagnostic.Error(DiagnosticCodes.Unexpected, "run", ex.Message, ex.ToString()) } };
		}
		_run = null;
		OnRunCompleted(run, result);
	}

	/// <summary>검증 진행 이벤트 → 로그 (작업 스레드에서 호출됨, LogView 가 모아서 표시)</summary>
	void AppendEvent(RunEvent e) => _log.AppendLine(e.Kind is RunEventKind.Output ? e.Message : "[ShipCheck] " + e.Message);

	void OnRunCompleted(ValidationRun run, RunResult result)
	{
		bool sameProject = run.Project.Equals(_project, StringComparison.OrdinalIgnoreCase);
		if (result.Outcome == RunOutcome.Canceled)
		{
			Diagnostic? leftover = result.Diagnostics?.FirstOrDefault(x => x.Code == DiagnosticCodes.LeftoverProcesses);
			_log.AppendLine(leftover == null
				? "[ShipCheck Monitor] 검증을 중지했습니다. UBT/컴파일러를 포함한 모든 하위 프로세스가 종료된 것을 확인했습니다."
				: $"[ShipCheck Monitor] 검증을 중지했지만 {leftover.Message}");
		}
		else
		{
			if (sameProject)
			{
				_shownRun = result;
				RenderErrors();
				if (!result.IsPass && _errorsList.Items.Count > 0)
				{
					_tabs.SelectedTab = _errorsPage;
				}
			}
			if (run.Trigger == "auto" && !result.IsPass)
			{
				// 같은 상태로 자동 검증을 반복하지 않는다 (코드가 바뀌면 다시 시도)
				_autoAttemptedKey = result.StateKey is { Length: > 0 } key ? key : _query?.StateKey;
			}
			if (result.Outcome is RunOutcome.Error or RunOutcome.Fail)
			{
				// 커밋 훅이 TortoiseSVN 창에 보여 주는 것과 같은 요약
				foreach (string line in RunReport.FailureText(result, new ShipCheckConfig { SkipTag = "" }).TrimEnd().Replace("\r", "").Split('\n'))
				{
					_log.AppendLine(line);
				}
			}
			Notify(run, result);
		}

		if (sameProject)
		{
			ReloadHistoryIfChanged();
		}
		UpdateAll();
		RequestRefresh();
	}

	void StopRun()
	{
		// 확인 창이 떠 있는 동안 실행이 끝나고 자동 검증이 새로 시작될 수 있으므로, 창을 띄운 시점의 실행만 중지한다
		ValidationRun? run = _run;
		if (run == null)
		{
			return;
		}
		if (run.Trigger != "auto" &&
			MessageBox.Show(this, "검증을 중지할까요?\n진행 중인 UBT/컴파일러도 함께 종료됩니다.", Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
		{
			return;
		}
		if (ReferenceEquals(_run, run))
		{
			run.Cancel();
		}
	}

	void StartPrime()
	{
		if (_project == null)
		{
			return;
		}
		string targets = _query == null ? "" : string.Join(", ", _query.Targets);
		if (MessageBox.Show(this, $"게임 타깃 전체 빌드를 실행합니다.\n{targets}\n\n최초 1회는 오래 걸릴 수 있습니다 (소스 빌드 엔진은 엔진 전체 빌드). 이후 검증은 증분 빌드로 빨라집니다.",
			Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Information) == DialogResult.OK)
		{
			_ = StartValidationAsync("gui", all: true, force: false);
		}
	}

	async Task TryAutoValidateAsync()
	{
		if (!_settings.AutoValidate || _run != null || _autoChecking || _queryRunning || _health != Health.NeedsValidation || _query == null)
		{
			return;
		}
		// 상태 키가 없으면(svn 없음/캐시 꺼짐) 통과 여부를 알 수 없어 무한 반복하므로 자동 검증하지 않는다
		if (_query.StateKey is not { Length: > 0 } key || key == _autoAttemptedKey)
		{
			return;
		}
		if ((DateTime.UtcNow - _lastCodeChangeUtc).TotalSeconds < _settings.IdleSeconds)
		{
			return;
		}

		_autoChecking = true;
		try
		{
			bool editorRunning = _settings.SkipAutoWhileEditorRunning && Integrations.IsUnrealEditorRunning();
			bool busy = editorRunning || await Task.Run(Integrations.IsUnrealBuildToolRunning);
			if (busy)
			{
				_statusText.Text = editorRunning ? "자동 검증 대기: 언리얼 에디터 실행 중" : "자동 검증 대기: 다른 UBT 빌드 실행 중";
				return;
			}
		}
		finally
		{
			_autoChecking = false;
		}
		if (_run == null && _query?.StateKey == key)
		{
			_autoAttemptedKey = key;
			await StartValidationAsync("auto", all: false, force: false);
		}
	}

	void SetAutoValidate(bool enabled)
	{
		_settings.AutoValidate = enabled;
		_settings.Save();
		_autoAttemptedKey = null;
		UpdateAll();
	}

	void Notify(ValidationRun run, RunResult result)
	{
		if (!_settings.NotifyOnResult || (ContainsFocus && run.Trigger != "auto"))
		{
			return;
		}
		if (run.Trigger == "auto" && result.IsPass)
		{
			// 자동 검증 통과는 조용히 (트레이 아이콘 색으로만 표시)
			return;
		}
		string projectName = Path.GetFileNameWithoutExtension(run.Project);
		int errorCount = BuildOutputParser.FromRun(result).Count;
		string text = result.IsPass ? $"{projectName}: 현재 워킹 카피가 게임 빌드 검증을 통과했습니다." : $"{projectName}: 오류 {errorCount}건 — 클릭하여 확인";
		_tray.ShowBalloonTip(5000, $"ShipCheck {result.ResultText}", text, result.IsPass ? ToolTipIcon.Info : ToolTipIcon.Error);
	}

	#endregion

	#region SVN / 편집기 / 도구

	async Task RunTortoiseAsync(string command, string? path = null, bool withPath = true)
	{
		try
		{
			string? target = withPath ? (path ?? WorkingCopyRoot()) : null;
			await Integrations.RunTortoiseAsync(command, target);
		}
		catch (Exception ex)
		{
			MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
			return;
		}
		if (withPath)
		{
			RequestRefresh();
		}
	}

	string? WorkingCopyRoot()
	{
		if (_query?.WcRoot is { Length: > 0 } root)
		{
			return root.Replace('/', '\\');
		}
		return ProjectDir;
	}

	async Task CommitAsync()
	{
		if (_project == null)
		{
			return;
		}
		if (_health is Health.NeedsValidation or Health.Failed)
		{
			// 훅이 이 프로젝트에 적용된 것이 확인된 경우에만 "차단된다"고 안내한다
			bool hookApplies = CurrentHookStatus().BlocksCommits;
			string hookNote = hookApplies
				? ""
				: "\n\n※ 이 프로젝트에 커밋 훅이 적용된 것이 확인되지 않아, 커밋해도 차단되지 않을 수 있습니다. 먼저 ▶ 검증을 권장합니다.";
			string message = _health == Health.Failed
				? (hookApplies ? "현재 변경 사항은 검증에 실패한 상태라 커밋 훅에서 차단됩니다." : "현재 변경 사항은 검증에 실패한 상태입니다.") + hookNote + "\n\n그래도 커밋 창을 열까요?"
				: (hookApplies ? "아직 검증하지 않은 변경 사항이 있습니다. 커밋할 때 훅이 검증합니다 (시간이 걸릴 수 있음)." : "아직 검증하지 않은 변경 사항이 있습니다.") + hookNote + "\n\n커밋 창을 열까요?";
			if (MessageBox.Show(this, message, Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
			{
				return;
			}
		}
		await RunTortoiseAsync("commit");
	}

	ChangeEntry? SelectedChange() => _changesList.SelectedItems.Count > 0 ? _changesList.SelectedItems[0].Tag as ChangeEntry : null;

	void OpenSelectedChange()
	{
		if (SelectedChange() is ChangeEntry change && File.Exists(change.Path))
		{
			Integrations.OpenInEditor(_settings.EditorCommand, change.Path, 1);
		}
	}

	void OpenSelectedError()
	{
		if (_errorsList.SelectedItems.Count > 0 && _errorsList.SelectedItems[0].Tag is Diagnostic error)
		{
			OpenError(error);
		}
	}

	void OpenError(Diagnostic error)
	{
		if (error.File == null || !File.Exists(error.File))
		{
			return;
		}
		try
		{
			Integrations.OpenInEditor(_settings.EditorCommand, error.File, Math.Max(error.Line ?? 1, 1));
		}
		catch (Exception ex)
		{
			MessageBox.Show(this, "편집기를 열 수 없습니다:\n" + ex.Message + "\n\n⚙ 설정에서 편집기 명령을 확인하세요.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
		}
	}

	static void CopySelected(ListView list, Func<ListViewItem, string> selector)
	{
		string text = string.Join(Environment.NewLine, list.SelectedItems.Cast<ListViewItem>().Select(selector));
		if (text.Length > 0)
		{
			Clipboard.SetText(text);
		}
	}

	void CopyHookCommand()
	{
		if (_project == null)
		{
			return;
		}
		string command = _engine.HookCommandLine(_project);
		Clipboard.SetText(command);
		MessageBox.Show(this,
			"커밋 훅 명령을 클립보드에 복사했습니다.\n\n" +
			"TortoiseSVN ▸ Settings ▸ Hook Scripts ▸ Add...\n" +
			"  • Hook Type: Pre-Commit Hook\n" +
			$"  • Working Copy Path: {WorkingCopyRoot()}\n" +
			"  • Command Line To Execute: (붙여넣기)\n" +
			"  • Wait for the script to finish: 체크\n\n" +
			command,
			Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
	}

	void OpenProjectSubfolder(string relative)
	{
		string? projectDir = ProjectDir;
		if (projectDir == null)
		{
			return;
		}
		string dir = Path.Combine(projectDir, relative);
		if (!Directory.Exists(dir))
		{
			dir = projectDir;
		}
		Integrations.ShowInExplorer(dir);
	}

	void ClearPassCache()
	{
		string? projectDir = ProjectDir;
		if (projectDir == null)
		{
			return;
		}
		if (MessageBox.Show(this, "통과 캐시를 지울까요?\n다음 검증과 커밋 훅은 다시 빌드합니다.", Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) == DialogResult.OK)
		{
			try
			{
				FileRunStore.ClearPassCache(projectDir);
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				MessageBox.Show(this, "통과 캐시를 지울 수 없습니다:\n" + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
			}
			RequestRefresh();
		}
	}

	void OpenSettings()
	{
		using SettingsForm form = new SettingsForm(_settings, _project, _query, _engine.ToolDir);
		if (form.ShowDialog(this) == DialogResult.OK)
		{
			_settings.Save();
			_autoAttemptedKey = null;
			UpdateAll();
			// 프로젝트 설정(타깃/정책)이 바뀌면 검증 상태도 달라진다
			RequestRefresh();
		}
	}

	#endregion

	#region 창 / 트레이

	void ShowFromTray()
	{
		_allowShow = true;
		Show();
		if (WindowState == FormWindowState.Minimized)
		{
			WindowState = FormWindowState.Normal;
		}
		Activate();
		RequestRefresh();
	}

	protected override void OnActivated(EventArgs e)
	{
		base.OnActivated(e);
		if (_project != null && _run == null && (DateTime.Now - _lastQueryTime).TotalSeconds > 10)
		{
			RequestRefresh();
		}
	}

	void ExitApplication()
	{
		_exiting = true;
		Close();
	}

	protected override void OnFormClosing(FormClosingEventArgs e)
	{
		if (!_exiting && _settings.MinimizeToTray && e.CloseReason == CloseReason.UserClosing)
		{
			e.Cancel = true;
			SaveWindowState();
			Hide();
			if (!_trayHintShown)
			{
				_trayHintShown = true;
				_tray.ShowBalloonTip(3000, "ShipCheck Monitor", "트레이에서 계속 감시합니다. 종료하려면 트레이 아이콘 ▸ 종료.", ToolTipIcon.Info);
			}
			return;
		}
		if (_run != null)
		{
			if (MessageBox.Show(this, "검증이 진행 중입니다. 중지하고 종료할까요?", Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK)
			{
				e.Cancel = true;
				_exiting = false;
				return;
			}
			_run.Cancel();
		}
		SaveWindowState();
		base.OnFormClosing(e);
	}

	void SaveWindowState()
	{
		if (!_allowShow)
		{
			return;
		}
		_settings.WindowMaximized = WindowState == FormWindowState.Maximized;
		Rectangle bounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
		_settings.WindowX = bounds.X;
		_settings.WindowY = bounds.Y;
		_settings.WindowWidth = bounds.Width;
		_settings.WindowHeight = bounds.Height;
		_settings.SplitterDistance = _split.SplitterDistance;
		_settings.Save();
	}

	protected override void Dispose(bool disposing)
	{
		if (disposing)
		{
			_showWaitHandle.Unregister(null);
			DisposeWatchers();
			_queryCancel?.Cancel();
			_refreshDebounce.Dispose();
			_periodicTimer.Dispose();
			_autoTimer.Dispose();
			_clockTimer.Dispose();
			_tray.Visible = false;
			_tray.Dispose();
		}
		base.Dispose(disposing);
	}

	#endregion
}
