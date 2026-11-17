namespace ADBDrive.Core.Localization;

/// <summary>
/// 词条表。中英两张表键必须一一对应（缺一个会回退到另一种语言，界面上就能看出混语言）。
/// 约定：key 用小写点分命名，{0}/{1} 是占位符。
/// </summary>
internal static class StringTables
{
    internal static readonly Dictionary<string, string> Zh = new(StringComparer.Ordinal)
    {
        // ===== 品牌 / 窗口 =====
        ["app.title"] = "ADBDrive · 远程 ADB 磁盘",
        ["app.footer"] = "ADBDrive · NativeAOT · Avalonia 12 · 远程 ADB 文件系统",
        ["titlebar.subtitle"] = "把远程 Android 设备挂载为 Windows 盘符",

        // ===== 窗口按钮 =====
        ["caption.settings"] = "设置",
        ["caption.minimize"] = "最小化",
        ["caption.maximize"] = "最大化",
        ["caption.restore"] = "还原",
        ["caption.close"] = "关闭",

        // ===== 设置面板 =====
        ["settings.title"] = "设置",
        ["settings.minimizeToTray"] = "关闭窗口时收进托盘",
        ["settings.startMinimized"] = "启动时最小化到托盘",
        ["settings.language"] = "语言",
        ["settings.openConfig"] = "打开配置目录",
        ["settings.openLogs"] = "打开日志目录",

        // ===== 语言选项 =====
        ["language.system"] = "跟随系统",
        ["language.zh"] = "简体中文",
        ["language.en"] = "English",

        // ===== 状态徽标 =====
        ["status.adbChecking"] = "adb 检测中…",
        ["status.adbReady"] = "adb 就绪",
        ["status.adbMissing"] = "未找到 adb",
        ["status.adbPathMissing"] = "未找到 adb.exe",
        ["status.dokanChecking"] = "Dokan 检测中…",
        ["status.dokanReady"] = "Dokan 就绪",
        ["status.dokanMissing"] = "Dokan 未安装",

        // ===== 顶部提示条 =====
        ["banner.dokanMissing"] = "{0}。安装 Dokan 驱动后才能把设备挂载成盘符。",
        ["banner.downloadDokan"] = "下载 Dokan",
        ["banner.recheck"] = "重新检测",
        ["banner.dismiss"] = "关闭提示",
        ["banner.needEndpoint"] = "请填写设备地址，例如 192.168.1.20:5555",
        ["banner.unauthorized"] = "设备未授权：请解锁手机并在弹窗中点击「允许」。之后会自动继续。",

        // ===== 设备列表 =====
        ["devices.title"] = "我的设备",
        ["devices.refresh"] = "刷新设备列表",
        ["devices.emptyTitle"] = "还没有设备",
        ["devices.emptyHint"] = "在右侧填写手机的 IP:端口，或从「ADB 设备」里直接挂载",
        ["devices.countNone"] = "暂无设备",
        ["devices.count"] = "{0} 台设备",
        ["devices.count.one"] = "{0} 台设备",
        ["devices.discoveredNone"] = "没有发现设备",
        ["devices.discoveredCount"] = "{0} 台在线/离线设备",
        ["devices.discoveredCount.one"] = "{0} 台在线/离线设备",
        ["devices.discoveredError"] = "读取设备列表失败",
        ["devices.serverUnavailable"] = "adb server 不可用",

        // ===== 设备卡片 =====
        ["device.rootLabel"] = "挂载根",
        ["device.rootTooltip"] = "可直接输入路径，或点右侧箭头选常用目录；已挂载时会自动卸载重挂",
        ["device.presetsTooltip"] = "常用挂载根",
        ["device.rootBadgeTooltip"] = "adbd 以 root 运行，可挂载 / 与 /data 等目录",
        ["device.autoConnect"] = "自动连接",
        ["device.autoConnectTooltip"] = "启动 ADBDrive 时自动连接这台设备",
        ["device.autoMount"] = "自动挂载",
        ["device.autoMountTooltip"] = "连接成功后自动挂载盘符",
        ["device.connect"] = "连接",
        ["device.mount"] = "挂载",
        ["device.open"] = "打开",
        ["device.unmount"] = "卸载",
        ["device.disconnect"] = "断开",
        ["device.remove"] = "从列表移除",
        ["device.preferredDrive"] = "首选盘符",
        ["device.more"] = "更多操作",
        ["device.openDrive"] = "打开盘符",
        ["device.settings"] = "设备设置",
        ["device.nameLabel"] = "备注",
        ["device.namePlaceholder"] = "可选，留空则显示机型名",
        ["device.letterLabel"] = "盘符",
        ["device.notMounted"] = "未挂载 · 点击设备行可打开设置",
        ["device.apply"] = "应用",
        ["device.cancel"] = "取消",
        ["device.identity"] = "身份 {0}",

        // ===== 常用挂载根 =====
        ["rootPreset.sdcard"] = "/sdcard（共享存储）",
        ["rootPreset.data"] = "/data（需 root）",
        ["rootPreset.system"] = "/system（需 root）",
        ["rootPreset.all"] = "/（全盘，需 root）",

        // ===== 发现设备 =====
        ["discovered.title"] = "ADB 设备",

        // ===== 添加设备 =====
        ["add.title"] = "添加远程设备",
        ["add.namePlaceholder"] = "备注名（可选）",
        ["add.rootTooltip"] = "可直接输入挂载根，或点右侧箭头选常用目录；非 root 设备只能访问 /sdcard 一类共享目录",
        ["add.connectAndMount"] = "连接并挂载",
        ["add.autoConnectOnStart"] = "启动时自动连接",
        ["add.autoMountAfterConnect"] = "连接后自动挂载",
        ["add.prerequisites"] = "前提",
        ["add.prerequisitesText"] = "手机开启「无线调试」，或先用 USB 执行 adb tcpip 5555；首次连接需在手机上确认授权；安装 Dokan 驱动后资源管理器才会出现盘符。",

        // ===== 日志 =====
        ["logs.title"] = "运行日志",
        ["logs.count"] = "{0} 条",
        ["logs.count.one"] = "{0} 条",
        ["logs.openFolder"] = "打开目录",
        ["logs.clear"] = "清空",
        ["logs.toggle"] = "收起 / 展开",
        ["logs.disabled"] = "未启用",
        ["logs.level.info"] = "信息及以上",
        ["logs.level.all"] = "全部",
        ["logs.level.warn"] = "仅警告",
        ["logs.level.error"] = "仅错误",
        ["logs.searchPlaceholder"] = "搜索日志",
        ["logs.searchClear"] = "清除搜索",
        ["logs.searchCount"] = "命中 {0} / 共 {1} 条",
        ["logs.noMatch"] = "没有匹配的日志",
        ["logs.copyAll"] = "复制全部",
        ["logs.copySelected"] = "复制所选（{0} 行）",
        ["logs.copySelected.one"] = "复制所选（1 行）",
        ["logs.copied"] = "已复制 {0} 行",
        ["logs.copied.one"] = "已复制 1 行",

        // ===== 盘符 =====
        ["drive.auto"] = "自动",

        // ===== 托盘 =====
        ["tray.show"] = "显示主窗口",
        ["tray.exit"] = "退出 ADBDrive",

        // ===== 会话状态 =====
        ["sessionState.disconnected"] = "未连接",
        ["sessionState.connecting"] = "连接中",
        ["sessionState.connected"] = "已连接",
        ["sessionState.mounted"] = "已挂载",
        ["sessionState.offline"] = "离线",
        ["sessionState.unauthorized"] = "待授权",
        ["sessionState.error"] = "错误",
        ["sessionState.unknown"] = "未知",

        // ===== adb 设备状态 =====
        ["adbState.online"] = "在线",
        ["adbState.offline"] = "离线",
        ["adbState.unauthorized"] = "未授权",
        ["adbState.recovery"] = "Recovery",
        ["adbState.bootloader"] = "Bootloader",
        ["adbState.sideload"] = "Sideload",
        ["adbState.noPermissions"] = "无权限",
        ["adbState.unknown"] = "未知",

        ["identity.unknown"] = "未知",

        // ===== 会话状态文案 =====
        ["session.disconnected"] = "未连接",
        ["session.connecting"] = "正在连接…",
        ["session.cancelled"] = "连接已取消",
        ["session.serverUnavailable"] = "adb server 不可用，请检查 adb.exe 路径",
        ["session.deviceNotFound"] = "设备未出现在 adb devices 中",
        ["session.unauthorized"] = "请在手机上确认「允许 USB 调试」授权",
        ["session.confirmAuthorization"] = "请在手机上确认授权",
        ["session.authorizationRevoked"] = "授权被撤销，请在手机上重新确认",
        ["session.deviceState"] = "设备状态: {0}",
        ["session.connected"] = "已连接 · {0} · {1}",
        ["session.mounted"] = "{0}: · {1}",
        ["session.unmounted"] = "已卸载",
        ["session.offlineReconnecting"] = "设备已离线，正在尝试重连…",
        ["session.offlineRetrying"] = "设备离线，重试中…",
        ["session.restored"] = "已恢复连接 · {0}:",
        ["session.reconnected"] = "已重连 · {0}",
        ["session.rootInaccessible"] = "根路径不存在或无法访问: {0}{1}",
        ["session.rootHintNonRoot"] = "（当前 adbd 非 root，sync 通道只能访问 /sdcard、/storage/emulated/0 等共享目录）",
        ["session.cannotAccess"] = "无法访问 {0}{1}",
        ["session.rootHintMissing"] = "，该目录不存在或没有读取权限",
        ["session.rootHintNeedsRoot"] = "，该目录需要 root（adb root / Magisk 授权）才能访问",
        ["session.rootChangedPending"] = "根路径已改为 {0}，连接后生效",
        ["session.rootChanged"] = "根路径已改为 {0}",
        ["session.rootSwitched"] = "已切换到 {0}（{1}: 已重挂）",
        ["session.rootSwitchFailed"] = "切换到 {0} 失败：{1}，已回退",
        ["session.notConnected"] = "设备未连接，无法挂载",
        ["session.noDriveLetter"] = "没有可用盘符",

        // ===== Dokan =====
        ["dokan.notStarted"] = "Dokan 驱动未安装或未启动（库版本 {0}）",
        ["dokan.ready"] = "Dokan 就绪（库 {0}，驱动 {1}）",
        ["dokan.dllMissing"] = "未找到 dokan2.dll，请先安装 Dokan 驱动",
        ["dokan.unavailable"] = "Dokan 不可用: {0}",
    };

    internal static readonly Dictionary<string, string> En = new(StringComparer.Ordinal)
    {
        // ===== brand / window =====
        ["app.title"] = "ADBDrive · Remote ADB Drive",
        ["app.footer"] = "ADBDrive · NativeAOT · Avalonia 12 · Remote ADB file system",
        ["titlebar.subtitle"] = "Mount a remote Android device as a Windows drive",

        // ===== caption buttons =====
        ["caption.settings"] = "Settings",
        ["caption.minimize"] = "Minimize",
        ["caption.maximize"] = "Maximize",
        ["caption.restore"] = "Restore",
        ["caption.close"] = "Close",

        // ===== settings panel =====
        ["settings.title"] = "Settings",
        ["settings.minimizeToTray"] = "Minimize to tray when closing",
        ["settings.startMinimized"] = "Start minimized to tray",
        ["settings.language"] = "Language",
        ["settings.openConfig"] = "Open config folder",
        ["settings.openLogs"] = "Open log folder",

        // ===== language options =====
        ["language.system"] = "Follow system",
        ["language.zh"] = "简体中文",
        ["language.en"] = "English",

        // ===== status chips =====
        ["status.adbChecking"] = "Checking adb…",
        ["status.adbReady"] = "adb ready",
        ["status.adbMissing"] = "adb not found",
        ["status.adbPathMissing"] = "adb.exe not found",
        ["status.dokanChecking"] = "Checking Dokan…",
        ["status.dokanReady"] = "Dokan ready",
        ["status.dokanMissing"] = "Dokan not installed",

        // ===== banner =====
        ["banner.dokanMissing"] = "{0}. Install the Dokan driver to mount devices as drives.",
        ["banner.downloadDokan"] = "Download Dokan",
        ["banner.recheck"] = "Check again",
        ["banner.dismiss"] = "Dismiss",
        ["banner.needEndpoint"] = "Enter the device address, for example 192.168.1.20:5555",
        ["banner.unauthorized"] = "Device not authorized. Unlock the phone and tap \"Allow\" in the prompt; it will continue automatically.",

        // ===== device list =====
        ["devices.title"] = "My devices",
        ["devices.refresh"] = "Refresh device list",
        ["devices.emptyTitle"] = "No devices yet",
        ["devices.emptyHint"] = "Enter the phone's IP:port on the right, or mount directly from \"ADB devices\"",
        ["devices.countNone"] = "No devices",
        ["devices.count"] = "{0} devices",
        ["devices.count.one"] = "{0} device",
        ["devices.discoveredNone"] = "No devices discovered",
        ["devices.discoveredCount"] = "{0} devices online/offline",
        ["devices.discoveredCount.one"] = "{0} device online/offline",
        ["devices.discoveredError"] = "Failed to read the device list",
        ["devices.serverUnavailable"] = "adb server unavailable",

        // ===== device card =====
        ["device.rootLabel"] = "Mount root",
        ["device.rootTooltip"] = "Type a path, or pick a common folder from the arrow menu; a mounted drive is unmounted and remounted automatically",
        ["device.presetsTooltip"] = "Common mount roots",
        ["device.rootBadgeTooltip"] = "adbd is running as root, so / and /data can be mounted",
        ["device.autoConnect"] = "Auto-connect",
        ["device.autoConnectTooltip"] = "Connect this device when ADBDrive starts",
        ["device.autoMount"] = "Auto-mount",
        ["device.autoMountTooltip"] = "Mount the drive once connected",
        ["device.connect"] = "Connect",
        ["device.mount"] = "Mount",
        ["device.open"] = "Open",
        ["device.unmount"] = "Unmount",
        ["device.disconnect"] = "Disconnect",
        ["device.remove"] = "Remove from list",
        ["device.preferredDrive"] = "Preferred drive letter",
        ["device.more"] = "More actions",
        ["device.openDrive"] = "Open drive",
        ["device.settings"] = "Device settings",
        ["device.nameLabel"] = "Name",
        ["device.namePlaceholder"] = "Optional; falls back to the model name",
        ["device.letterLabel"] = "Drive letter",
        ["device.notMounted"] = "Not mounted · click the row to open settings",
        ["device.apply"] = "Apply",
        ["device.cancel"] = "Cancel",
        ["device.identity"] = "Identity {0}",

        // ===== mount root presets =====
        ["rootPreset.sdcard"] = "/sdcard (shared storage)",
        ["rootPreset.data"] = "/data (root required)",
        ["rootPreset.system"] = "/system (root required)",
        ["rootPreset.all"] = "/ (whole disk, root required)",

        // ===== discovered devices =====
        ["discovered.title"] = "ADB devices",

        // ===== add device =====
        ["add.title"] = "Add a remote device",
        ["add.namePlaceholder"] = "Name (optional)",
        ["add.rootTooltip"] = "Type a mount root, or pick one from the arrow menu; a non-root device can only reach shared storage such as /sdcard",
        ["add.connectAndMount"] = "Connect & mount",
        ["add.autoConnectOnStart"] = "Auto-connect on start",
        ["add.autoMountAfterConnect"] = "Auto-mount after connect",
        ["add.prerequisites"] = "Requirements",
        ["add.prerequisitesText"] = "Enable Wireless debugging on the phone, or run \"adb tcpip 5555\" over USB first. Confirm the authorization prompt on the phone the first time. The drive shows up in Explorer only after the Dokan driver is installed.",

        // ===== log =====
        ["logs.title"] = "Activity log",
        ["logs.count"] = "{0} entries",
        ["logs.count.one"] = "{0} entry",
        ["logs.openFolder"] = "Open folder",
        ["logs.clear"] = "Clear",
        ["logs.toggle"] = "Collapse / expand",
        ["logs.disabled"] = "Disabled",
        ["logs.level.info"] = "Info and above",
        ["logs.level.all"] = "Everything",
        ["logs.level.warn"] = "Warnings only",
        ["logs.level.error"] = "Errors only",
        ["logs.searchPlaceholder"] = "Search log",
        ["logs.searchClear"] = "Clear search",
        ["logs.searchCount"] = "{0} of {1} matching",
        ["logs.noMatch"] = "No matching entries",
        ["logs.copyAll"] = "Copy all",
        ["logs.copySelected"] = "Copy {0} selected rows",
        ["logs.copySelected.one"] = "Copy 1 selected row",
        ["logs.copied"] = "Copied {0} lines",
        ["logs.copied.one"] = "Copied 1 line",

        // ===== drive letters =====
        ["drive.auto"] = "Auto",

        // ===== tray =====
        ["tray.show"] = "Show main window",
        ["tray.exit"] = "Exit ADBDrive",

        // ===== session state =====
        ["sessionState.disconnected"] = "Disconnected",
        ["sessionState.connecting"] = "Connecting",
        ["sessionState.connected"] = "Connected",
        ["sessionState.mounted"] = "Mounted",
        ["sessionState.offline"] = "Offline",
        ["sessionState.unauthorized"] = "Authorization needed",
        ["sessionState.error"] = "Error",
        ["sessionState.unknown"] = "Unknown",

        // ===== adb device state =====
        ["adbState.online"] = "Online",
        ["adbState.offline"] = "Offline",
        ["adbState.unauthorized"] = "Unauthorized",
        ["adbState.recovery"] = "Recovery",
        ["adbState.bootloader"] = "Bootloader",
        ["adbState.sideload"] = "Sideload",
        ["adbState.noPermissions"] = "No permissions",
        ["adbState.unknown"] = "Unknown",

        ["identity.unknown"] = "unknown",

        // ===== session messages =====
        ["session.disconnected"] = "Not connected",
        ["session.connecting"] = "Connecting…",
        ["session.cancelled"] = "Connection cancelled",
        ["session.serverUnavailable"] = "adb server unavailable; check the adb.exe path",
        ["session.deviceNotFound"] = "Device did not show up in adb devices",
        ["session.unauthorized"] = "Confirm \"Allow USB debugging\" on the phone",
        ["session.confirmAuthorization"] = "Confirm the authorization on the phone",
        ["session.authorizationRevoked"] = "Authorization revoked; confirm it again on the phone",
        ["session.deviceState"] = "Device state: {0}",
        ["session.connected"] = "Connected · {0} · {1}",
        ["session.mounted"] = "{0}: · {1}",
        ["session.unmounted"] = "Unmounted",
        ["session.offlineReconnecting"] = "Device went offline; reconnecting…",
        ["session.offlineRetrying"] = "Device offline; retrying…",
        ["session.restored"] = "Connection restored · {0}:",
        ["session.reconnected"] = "Reconnected · {0}",
        ["session.rootInaccessible"] = "Mount root missing or inaccessible: {0}{1}",
        ["session.rootHintNonRoot"] = " (adbd is not root; the sync channel can only reach shared folders such as /sdcard and /storage/emulated/0)",
        ["session.cannotAccess"] = "Cannot access {0}{1}",
        ["session.rootHintMissing"] = ", the folder does not exist or is not readable",
        ["session.rootHintNeedsRoot"] = ", this folder needs root (adb root / Magisk grant) to access",
        ["session.rootChangedPending"] = "Mount root set to {0}; it applies after connecting",
        ["session.rootChanged"] = "Mount root set to {0}",
        ["session.rootSwitched"] = "Switched to {0} ({1}: remounted)",
        ["session.rootSwitchFailed"] = "Failed to switch to {0}: {1}; rolled back",
        ["session.notConnected"] = "Device is not connected; cannot mount",
        ["session.noDriveLetter"] = "No free drive letter",

        // ===== Dokan =====
        ["dokan.notStarted"] = "Dokan driver is not installed or not running (library {0})",
        ["dokan.ready"] = "Dokan ready (library {0}, driver {1})",
        ["dokan.dllMissing"] = "dokan2.dll not found; install the Dokan driver first",
        ["dokan.unavailable"] = "Dokan unavailable: {0}",
    };
}
