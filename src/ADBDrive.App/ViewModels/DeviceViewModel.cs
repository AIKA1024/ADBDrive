using System.Collections.ObjectModel;
using System.Diagnostics;
using ADBDrive.Core.Config;
using ADBDrive.Core.Localization;
using ADBDrive.Core.Mount;
using ADBDrive.Core.Sessions;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ADBDrive.ViewModels;

/// <summary>设备卡片视图模型。</summary>
public sealed partial class DeviceViewModel : ObservableObject
{
    private static readonly IBrush SuccessBrush = Brush.Parse("#4ADE80");
    private static readonly IBrush WarningBrush = Brush.Parse("#FBBF24");
    private static readonly IBrush DangerBrush = Brush.Parse("#F87171");
    private static readonly IBrush InfoBrush = Brush.Parse("#60A5FA");
    private static readonly IBrush MutedBrush = Brush.Parse("#6E747D");

    private readonly DeviceSession _session;
    private readonly Func<Task> _afterAction;

    [ObservableProperty]
    private string? _errorMessage;

    /// <summary>是否正在行内编辑挂载根。</summary>
    [ObservableProperty]
    private bool _isEditingRoot;

    /// <summary>编辑中的挂载根草稿（确定后才写回，避免每敲一个字就重挂）。</summary>
    [ObservableProperty]
    private string _rootDraft = string.Empty;

    /// <summary>编辑中的备注草稿（留空表示不用备注，显示时会退回机型名）。</summary>
    [ObservableProperty]
    private string _nameDraft = string.Empty;

    public DriveOption? SelectedDriveLetter
    {
        get
        {
            var letter = _session.Config.DriveLetter;
            var target = string.IsNullOrWhiteSpace(letter) ? (char?)null : char.ToUpperInvariant(letter[0]);
            return DriveLetterOptions.FirstOrDefault(option => option.Letter == target);
        }
        set
        {
            if (value is null)
            {
                return;
            }

            var current = SelectedDriveLetter;
            if (current is not null && current.Letter == value.Letter)
            {
                return;
            }

            _session.Config.DriveLetter = value.Letter?.ToString();
            SyncDriveSelection();
            OnPropertyChanged();
            _ = _afterAction();
        }
    }

    public DeviceViewModel(DeviceSession session, Func<Task> afterAction)
    {
        _session = session;
        _afterAction = afterAction;
        DriveLetterOptions = DriveOption.CreateAll();
        foreach (var option in DriveLetterOptions)
        {
            // 右键菜单直接绑每一项自己的命令，省掉相对源绑定。
            option.SelectCommand = SelectDriveCommand;
        }

        SyncDriveSelection();
    }

    public DeviceSession Session => _session;

    public ObservableCollection<DriveOption> DriveLetterOptions { get; }

    /// <summary>显示名：备注 → 机型名 → 端点，只显示一个。</summary>
    public string Name => _session.DisplayName;

    public string Endpoint => _session.Endpoint;

    /// <summary>悬停提示：把被顶掉的机型名和端点补全，方便核对是哪台机器。</summary>
    public string NameTooltip
    {
        get
        {
            var model = _session.ModelName;
            if (string.IsNullOrEmpty(model) || string.Equals(model, Name, StringComparison.OrdinalIgnoreCase))
            {
                return Endpoint;
            }

            return string.Equals(model, Endpoint, StringComparison.OrdinalIgnoreCase)
                ? Endpoint
                : $"{model} · {Endpoint}";
        }
    }

    /// <summary>挂载根路径。修改后立刻切到新的设备目录（需先卸载盘符）。</summary>
    public string RootPath
    {
        get => _session.Config.RootPath;
        set
        {
            // 忽略空值：ComboBox 初始化 / 刷新时可能先把 Text 清空，
            // 若不拦住会把用户配置的 /data 之类误改回默认值。
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            var normalized = DeviceSession.NormalizeRootPath(value);
            if (string.Equals(normalized, _session.Config.RootPath, StringComparison.Ordinal))
            {
                return;
            }

            _ = ApplyRootPathAsync(normalized);
        }
    }

    /// <summary>启动 ADBDrive 时自动连接这台设备。</summary>
    public bool AutoConnect
    {
        get => _session.Config.AutoConnect;
        set
        {
            if (_session.Config.AutoConnect == value)
            {
                return;
            }

            _session.Config.AutoConnect = value;
            OnPropertyChanged();
            _ = _afterAction();
        }
    }

    /// <summary>连接成功后自动挂载盘符。</summary>
    public bool AutoMount
    {
        get => _session.Config.AutoMount;
        set
        {
            if (_session.Config.AutoMount == value)
            {
                return;
            }

            _session.Config.AutoMount = value;
            OnPropertyChanged();
            _ = _afterAction();
        }
    }

    /// <summary>连接过程中不允许改；已挂载时会自动重挂，所以也允许。</summary>
    public bool CanChangeRoot => !IsBusy;

    /// <summary>adbd 是否为 root（决定能否访问 /data、/system、/）。</summary>
    public bool IsRootIdentity => _session.IsRoot;

    public string IdentityText => _session.IdentityText;

    private void SyncDriveSelection()
    {
        var current = SelectedDriveLetter;
        foreach (var option in DriveLetterOptions)
        {
            option.IsSelected = ReferenceEquals(option, current);
        }
    }

    [RelayCommand]
    private void SelectDrive(DriveOption? option)
    {
        if (option is not null)
        {
            SelectedDriveLetter = option;
        }
    }

    /// <summary>右键行：展开 / 收起设备设置（收起时丢弃草稿）。</summary>
    [RelayCommand]
    private void ToggleSettings()
    {
        if (IsEditingRoot)
        {
            CancelSettings();
            return;
        }

        BeginEditSettings();
    }

    /// <summary>进入行内设备设置（备注 + 盘符 + 挂载根）。</summary>
    [RelayCommand]
    private void BeginEditSettings()
    {
        RootDraft = RootPath;
        NameDraft = _session.Config.DisplayName ?? string.Empty;
        ErrorMessage = null;
        IsEditingRoot = true;
    }

    /// <summary>应用草稿：备注即时生效，挂载根变了才走「卸载 → 改根 → 重挂」。</summary>
    [RelayCommand]
    private void ApplySettings()
    {
        IsEditingRoot = false;

        var name = NameDraft.Trim();
        var normalized = string.IsNullOrEmpty(name) ? null : name;
        if (!string.Equals(normalized, _session.Config.DisplayName, StringComparison.Ordinal))
        {
            _session.Config.DisplayName = normalized;
            NameDraft = name;
            OnPropertyChanged(nameof(Name));
            _ = _afterAction();
        }

        RootPath = RootDraft;
    }

    [RelayCommand]
    private void CancelSettings()
    {
        IsEditingRoot = false;
        RootDraft = RootPath;
        NameDraft = _session.Config.DisplayName ?? string.Empty;
    }

    /// <summary>点击预设路径（来自根路径旁的下拉菜单）。</summary>
    [RelayCommand]
    private void PickRoot(string? root)
    {
        if (!string.IsNullOrWhiteSpace(root))
        {
            RootPath = root;
        }
    }

    private async Task ApplyRootPathAsync(string root)
    {
        ErrorMessage = null;
        try
        {
            var (ok, message) = await _session.ChangeRootPathAsync(root);
            if (!ok)
            {
                ErrorMessage = message;
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            await _afterAction();
        }
    }

    public string StatusText => _session.StatusMessage;

    public string StateText => Loc.T(_session.State switch
    {
        SessionState.Disconnected => "sessionState.disconnected",
        SessionState.Connecting => "sessionState.connecting",
        SessionState.Connected => "sessionState.connected",
        SessionState.Mounted => "sessionState.mounted",
        SessionState.Offline => "sessionState.offline",
        SessionState.Unauthorized => "sessionState.unauthorized",
        SessionState.Error => "sessionState.error",
        _ => "sessionState.unknown",
    });

    public IBrush StatusBrush => _session.State switch
    {
        SessionState.Connected or SessionState.Mounted => SuccessBrush,
        SessionState.Connecting => InfoBrush,
        SessionState.Unauthorized => WarningBrush,
        SessionState.Offline or SessionState.Error => DangerBrush,
        _ => MutedBrush,
    };

    public bool IsMounted => _session.State == SessionState.Mounted;

    public bool IsBusy => _session.State == SessionState.Connecting;

    public string DriveText => _session.Mount is { } mount ? $"{mount.DriveLetter}:" : "—";

    public bool CanConnect => _session.State is SessionState.Disconnected or SessionState.Error
        or SessionState.Offline or SessionState.Unauthorized;

    public bool CanMount => _session.State == SessionState.Connected;

    public bool CanUnmount => _session.State == SessionState.Mounted;

    public bool CanDisconnect => _session.State is SessionState.Connected or SessionState.Mounted
        or SessionState.Offline or SessionState.Unauthorized or SessionState.Error;

    public bool CanChangeDriveLetter => !IsMounted;

    public bool CanOpenDrive => IsMounted;

    /// <summary>身份信息（root / uid）。</summary>
    public string SubtitleText => Loc.F("device.identity", _session.IdentityText);

    /// <summary>单行卡片右侧那句补充信息：已连接时是挂载根，否则是状态说明。</summary>
    public string DetailText
    {
        get
        {
            if (_session.State is SessionState.Connected or SessionState.Mounted)
            {
                return RootPath;
            }

            // 状态词和左边那列一样时留空，避免出现「未连接 未连接」。
            var status = StatusText;
            return string.Equals(status, StateText, StringComparison.Ordinal) ? string.Empty : status;
        }
    }

    public void Refresh()
    {
        // 「自动 / Auto」这类文案跟着语言走，其余属性靠空名字一次性全部失效。
        foreach (var option in DriveLetterOptions)
        {
            option.Refresh();
        }

        SyncDriveSelection();
        OnPropertyChanged(string.Empty);
    }

    [RelayCommand]
    private async Task ConnectAsync()
    {
        ErrorMessage = null;
        try
        {
            await _session.ConnectAsync();
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            await _afterAction();
        }
    }

    [RelayCommand]
    private async Task MountAsync()
    {
        ErrorMessage = null;
        try
        {
            await _session.MountAsync();
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            await _afterAction();
        }
    }

    [RelayCommand]
    private async Task UnmountAsync()
    {
        ErrorMessage = null;
        try
        {
            await _session.UnmountAsync();
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            await _afterAction();
        }
    }

    [RelayCommand]
    private async Task DisconnectAsync()
    {
        ErrorMessage = null;
        try
        {
            await _session.DisconnectAsync();
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            await _afterAction();
        }
    }

    [RelayCommand]
    private void OpenDrive()
    {
        if (_session.Mount is not { } mount)
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"{mount.DriveLetter}:") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    [RelayCommand]
    private async Task RemoveAsync()
    {
        ErrorMessage = null;
        try
        {
            await _session.DisconnectAsync();
        }
        catch
        {
            // 删除时忽略断开失败
        }

        RemoveRequested?.Invoke(this);
        await Task.CompletedTask;
    }

    public event Action<DeviceViewModel>? RemoveRequested;
}
