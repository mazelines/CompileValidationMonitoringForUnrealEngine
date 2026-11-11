using ShipCheck.Core;
using ShipCheck.Infrastructure;

namespace ShipCheckMonitor;

/// <summary>
/// 설정 창: [일반] 앱 동작 / [프로젝트] 검증 타깃과 정책 (ShipCheck.&lt;프로젝트명&gt;.config.json 으로 저장)
/// </summary>
sealed class SettingsForm : Form
{
	readonly AppSettings _settings;
	readonly string? _project;
	readonly string? _projectName;
	readonly string? _configPath;
	readonly string? _detectedEngine;
	readonly string _toolDir;
	readonly ShipCheckConfig? _config;

	// 일반
	readonly CheckBox _autoValidate = new() { Text = "코드 변경 후 유휴 시간이 지나면 자동 검증 (커밋 시 캐시로 즉시 통과)", AutoSize = true };
	readonly NumericUpDown _idleSeconds = new() { Minimum = 15, Maximum = 3600, Increment = 15, Width = 80 };
	readonly CheckBox _skipWhileEditor = new() { Text = "언리얼 에디터가 실행 중이면 자동 검증하지 않음 (PIE/Live Coding 과 CPU 경쟁 방지)", AutoSize = true };
	readonly CheckBox _notify = new() { Text = "검증 결과를 트레이 알림으로 표시", AutoSize = true };
	readonly CheckBox _minimizeToTray = new() { Text = "창을 닫으면 트레이로 최소화", AutoSize = true };
	readonly TextBox _editorCommand = new() { Width = 520 };

	// 프로젝트
	readonly TextBox _engineDir = new() { Width = 430 };
	readonly DataGridView _targets = new()
	{
		Height = 120,
		Width = 560,
		AllowUserToAddRows = true,
		AllowUserToDeleteRows = true,
		RowHeadersWidth = 24,
		AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
		BackgroundColor = SystemColors.Window,
	};
	readonly CheckBox _singleFile = new() { Text = "변경 파일 단독 컴파일 (unity 빌드에 가려진 include 누락 검출)", AutoSize = true };
	readonly CheckBox _includeHeaders = new() { Text = "헤더(.h)도 단독 컴파일 (UE5)", AutoSize = true };
	readonly NumericUpDown _maxFiles = new() { Minimum = 1, Maximum = 500, Width = 80 };
	readonly CheckBox _skipEditorOnly = new() { Text = "에디터 전용 모듈만 바뀐 커밋은 빌드 생략", AutoSize = true };
	readonly ComboBox _uncommitted = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200 };
	readonly TextBox _skipTag = new() { Width = 200 };
	readonly CheckBox _useCache = new() { Text = "통과 캐시 사용", AutoSize = true };
	readonly TextBox _extraArgs = new() { Width = 430, Height = 54, Multiline = true, ScrollBars = ScrollBars.Vertical, AcceptsReturn = true };

	/// <param name="project">선택한 .uproject. 상태 조회(엔진 탐색 등)가 실패해도 설정으로 복구할 수 있도록 조회 결과와 독립적으로 받는다.</param>
	public SettingsForm(AppSettings settings, string? project, QueryResult? query, string toolDir)
	{
		_settings = settings;
		_project = project;
		_toolDir = toolDir;
		if (project != null)
		{
			_projectName = Path.GetFileNameWithoutExtension(project);
			// 스크립트와 같은 우선순위: ShipCheck.<프로젝트명>.config.json > ShipCheck.config.json
			string perProject = Path.Combine(toolDir, ShipCheckConfig.PerProjectFileName(_projectName));
			_configPath = query?.ConfigPath is { Length: > 0 } applied ? applied
				: File.Exists(perProject) ? perProject
				: Path.Combine(toolDir, ShipCheckConfig.DefaultFileName);
			_detectedEngine = query?.EngineRoot;
			try
			{
				_config = File.Exists(_configPath) ? ShipCheckConfig.Parse(File.ReadAllText(_configPath), _configPath) : new ShipCheckConfig();
			}
			catch (Exception ex) when (ex is IOException or ShipCheckException or UnauthorizedAccessException)
			{
				MessageBox.Show($"설정 파일을 읽을 수 없어 기본값으로 엽니다:\n{_configPath}\n{ex.Message}", "설정", MessageBoxButtons.OK, MessageBoxIcon.Warning);
				_config = new ShipCheckConfig();
			}
		}

		Text = "설정";
		FormBorderStyle = FormBorderStyle.FixedDialog;
		MaximizeBox = MinimizeBox = false;
		StartPosition = FormStartPosition.CenterParent;
		ClientSize = new Size(640, 560);

		TabControl tabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point(12, 5) };
		tabs.TabPages.Add(BuildGeneralPage());
		tabs.TabPages.Add(BuildProjectPage());

		FlowLayoutPanel buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 44, Padding = new Padding(8) };
		Button cancel = new Button { Text = "취소", DialogResult = DialogResult.Cancel, Width = 90 };
		Button ok = new Button { Text = "확인", Width = 90 };
		ok.Click += (_, _) => { if (Apply()) { DialogResult = DialogResult.OK; } };
		buttons.Controls.Add(cancel);
		buttons.Controls.Add(ok);
		AcceptButton = ok;
		CancelButton = cancel;

		Controls.Add(tabs);
		Controls.Add(buttons);
	}

	static FlowLayoutPanel Column() => new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(12) };

	static Label Caption(string text, bool bold = false) => new Label
	{
		Text = text,
		AutoSize = true,
		Margin = new Padding(0, bold ? 12 : 4, 0, 2),
		Font = bold ? new Font(SystemFonts.MessageBoxFont!, FontStyle.Bold) : SystemFonts.MessageBoxFont!,
		ForeColor = bold ? SystemColors.ControlText : SystemColors.GrayText,
		MaximumSize = new Size(580, 0),
	};

	static FlowLayoutPanel Row(params Control[] controls)
	{
		FlowLayoutPanel row = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Margin = new Padding(0, 2, 0, 2) };
		row.Controls.AddRange(controls);
		return row;
	}

	TabPage BuildGeneralPage()
	{
		TabPage page = new TabPage("일반");
		FlowLayoutPanel column = Column();

		_autoValidate.Checked = _settings.AutoValidate;
		_idleSeconds.Value = Math.Clamp(_settings.IdleSeconds, (int)_idleSeconds.Minimum, (int)_idleSeconds.Maximum);
		_skipWhileEditor.Checked = _settings.SkipAutoWhileEditorRunning;
		_notify.Checked = _settings.NotifyOnResult;
		_minimizeToTray.Checked = _settings.MinimizeToTray;
		_editorCommand.Text = _settings.EditorCommand;

		column.Controls.Add(Caption("자동 검증", bold: true));
		column.Controls.Add(_autoValidate);
		column.Controls.Add(Row(new Label { Text = "유휴 시간(초)", AutoSize = true, Margin = new Padding(20, 6, 6, 0) }, _idleSeconds));
		column.Controls.Add(_skipWhileEditor);
		column.Controls.Add(Caption("다른 UBT 빌드(IDE, Live Coding)가 실행 중이면 끝날 때까지 미룹니다. 같은 상태로 실패하면 코드가 바뀔 때까지 다시 시도하지 않습니다."));

		column.Controls.Add(Caption("창", bold: true));
		column.Controls.Add(_notify);
		column.Controls.Add(_minimizeToTray);

		column.Controls.Add(Caption("오류 위치를 여는 편집기 명령", bold: true));
		Button detect = new Button { Text = "자동 감지", AutoSize = true };
		detect.Click += (_, _) => _editorCommand.Text = EditorCommands.Detect();
		column.Controls.Add(Row(_editorCommand, detect));
		column.Controls.Add(Caption("{file}, {line} 이 치환됩니다. 비우면 기본 프로그램으로 파일을 엽니다.\n예) VS Code: \"...\\code.cmd\" -g \"{file}:{line}\"   Rider: \"...\\rider64.exe\" --line {line} \"{file}\"\n    Visual Studio: \"...\\devenv.exe\" /edit \"{file}\" /command \"Edit.GoTo {line}\""));

		page.Controls.Add(column);
		return page;
	}

	TabPage BuildProjectPage()
	{
		TabPage page = new TabPage(_projectName == null ? "프로젝트" : $"프로젝트: {_projectName}");
		FlowLayoutPanel column = Column();
		if (_projectName == null || _config == null)
		{
			column.Controls.Add(Caption("먼저 프로젝트를 추가하세요."));
			page.Controls.Add(column);
			return page;
		}

		string savePath = Path.Combine(_toolDir, ShipCheckConfig.PerProjectFileName(_projectName));
		column.Controls.Add(Caption($"현재 적용 중: {_configPath}\n저장 위치: {savePath}  (커밋 훅과 명령줄도 같은 설정을 사용합니다)"));

		column.Controls.Add(Caption("엔진 경로 (비우면 .uproject 의 EngineAssociation 으로 자동 탐색)", bold: true));
		Button browse = new Button { Text = "찾아보기...", AutoSize = true };
		browse.Click += (_, _) =>
		{
			using FolderBrowserDialog dialog = new FolderBrowserDialog { Description = "엔진 루트 (Engine 폴더의 상위) 선택", UseDescriptionForTitle = true };
			if (dialog.ShowDialog(this) == DialogResult.OK)
			{
				_engineDir.Text = dialog.SelectedPath;
			}
		};
		_engineDir.Text = _config.EngineDir;
		_engineDir.PlaceholderText = _detectedEngine ?? "자동 탐색 실패 — 엔진 루트를 지정하세요";
		column.Controls.Add(Row(_engineDir, browse));

		column.Controls.Add(Caption("검증 타깃 (이름을 비우면 프로젝트 이름 = 게임 타깃)", bold: true));
		_targets.Columns.Add(new DataGridViewTextBoxColumn { Name = "Name", HeaderText = "타깃 이름" });
		_targets.Columns.Add(new DataGridViewComboBoxColumn { Name = "Platform", HeaderText = "플랫폼", DataSource = new[] { "Win64", "Linux", "LinuxArm64", "Android", "IOS", "Mac", "PS5", "XSX", "Switch" }, FlatStyle = FlatStyle.Flat });
		_targets.Columns.Add(new DataGridViewComboBoxColumn { Name = "Configuration", HeaderText = "구성", DataSource = new[] { "Shipping", "Test", "Development", "DebugGame" }, FlatStyle = FlatStyle.Flat });
		_targets.DefaultValuesNeeded += (_, e) => { e.Row.Cells["Platform"].Value = "Win64"; e.Row.Cells["Configuration"].Value = "Shipping"; };
		_targets.DataError += (_, e) => e.ThrowException = false;
		foreach (TargetConfig target in _config.Targets)
		{
			_targets.Rows.Add(target.Name, target.Platform, target.Configuration);
		}
		column.Controls.Add(_targets);

		column.Controls.Add(Caption("검증 정책", bold: true));
		_singleFile.Checked = _config.SingleFileCheck.Enabled;
		_includeHeaders.Checked = _config.SingleFileCheck.IncludeHeaders;
		_maxFiles.Value = Math.Clamp(_config.SingleFileCheck.MaxFiles, 1, 500);
		_skipEditorOnly.Checked = _config.SkipEditorOnlyChanges;
		_uncommitted.Items.AddRange(new object[] { "Warn", "Fail" });
		_uncommitted.SelectedItem = _config.UncommittedSourceChanges == "Fail" ? "Fail" : "Warn";
		_skipTag.Text = _config.SkipTag;
		_useCache.Checked = _config.UseCache;
		// 인자 하나당 한 줄: 공백이 들어간 인자도 그대로 저장된다
		_extraArgs.Text = string.Join(Environment.NewLine, _config.ExtraUbtArgs);
		_singleFile.CheckedChanged += (_, _) => _includeHeaders.Enabled = _maxFiles.Enabled = _singleFile.Checked;
		_includeHeaders.Enabled = _maxFiles.Enabled = _singleFile.Checked;

		column.Controls.Add(_singleFile);
		column.Controls.Add(Row(new Label { Width = 18 }, _includeHeaders, new Label { Text = "최대 파일 수", AutoSize = true, Margin = new Padding(16, 6, 6, 0) }, _maxFiles));
		column.Controls.Add(_skipEditorOnly);
		column.Controls.Add(_useCache);
		column.Controls.Add(Row(new Label { Text = "커밋에서 빠진 로컬 코드 변경", AutoSize = true, Margin = new Padding(0, 6, 8, 0) }, _uncommitted,
			new Label { Text = "Warn=경고, Fail=커밋 차단", AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(8, 6, 0, 0) }));
		column.Controls.Add(Row(new Label { Text = "긴급 우회 태그", AutoSize = true, Margin = new Padding(0, 6, 8, 0) }, _skipTag,
			new Label { Text = "비우면 우회 불가", AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(8, 6, 0, 0) }));
		column.Controls.Add(Row(new Label { Text = "UBT 추가 인자\n(한 줄에 하나)", AutoSize = true, Margin = new Padding(0, 6, 8, 0) }, _extraArgs));

		page.Controls.Add(column);
		return page;
	}

	bool Apply()
	{
		_settings.AutoValidate = _autoValidate.Checked;
		_settings.IdleSeconds = (int)_idleSeconds.Value;
		_settings.SkipAutoWhileEditorRunning = _skipWhileEditor.Checked;
		_settings.NotifyOnResult = _notify.Checked;
		_settings.MinimizeToTray = _minimizeToTray.Checked;
		_settings.EditorCommand = _editorCommand.Text.Trim();

		if (_projectName == null || _config == null)
		{
			return true;
		}

		List<TargetConfig> targets = new List<TargetConfig>();
		foreach (DataGridViewRow row in _targets.Rows)
		{
			if (row.IsNewRow)
			{
				continue;
			}
			targets.Add(new TargetConfig
			{
				Name = (row.Cells["Name"].Value as string ?? "").Trim(),
				Platform = row.Cells["Platform"].Value as string ?? "Win64",
				Configuration = row.Cells["Configuration"].Value as string ?? "Shipping",
			});
		}
		if (targets.Count == 0)
		{
			MessageBox.Show(this, "검증 타깃이 최소 1개 필요합니다.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
			return false;
		}

		string engineDir = _engineDir.Text.Trim();
		if (engineDir.Length > 0 && !File.Exists(Path.Combine(engineDir, "Engine", "Build", "BatchFiles", "Build.bat")) &&
			!File.Exists(Path.Combine(engineDir, "Build", "BatchFiles", "Build.bat")))
		{
			MessageBox.Show(this, "엔진 경로에서 Engine\\Build\\BatchFiles\\Build.bat 를 찾을 수 없습니다.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
			return false;
		}

		_config.EngineDir = engineDir;
		_config.Targets = targets;
		_config.SingleFileCheck = new SingleFileConfig { Enabled = _singleFile.Checked, IncludeHeaders = _includeHeaders.Checked, MaxFiles = (int)_maxFiles.Value };
		_config.SkipEditorOnlyChanges = _skipEditorOnly.Checked;
		_config.UncommittedSourceChanges = _uncommitted.SelectedItem as string ?? "Warn";
		_config.SkipTag = _skipTag.Text.Trim();
		_config.UseCache = _useCache.Checked;
		_config.ExtraUbtArgs = _extraArgs.Text.Split('\n').Select(x => x.TrimEnd('\r').Trim()).Where(x => x.Length > 0).ToList();
		// 프로젝트 경로는 -Project 로 넘기므로 설정 파일에는 고정하지 않는다
		_config.UProject = "";

		try
		{
			FileRunStore.WriteAtomic(Path.Combine(_toolDir, ShipCheckConfig.PerProjectFileName(_projectName)), _config.ToJson());
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			MessageBox.Show(this, "프로젝트 설정을 저장할 수 없습니다:\n" + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
			return false;
		}
		return true;
	}
}
