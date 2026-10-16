// Windows GUI 验收基建：UI Automation（UIA3 COM）薄封装驱动标准向导
// （NSIS #32770 向导帧、MSI MsiDialog* 对话框）。
// 只在交互式桌面会话可用（explorer 在跑、UIA 可达顶层窗口）；各腿以 Assert.SkipWhen 自行门禁。
using System.Runtime.InteropServices;
using Interop.UIAutomationClient;

internal static class WindowsDesktop
{
    private static IUIAutomation? _automation;
    private static IUIAutomation Automation => _automation ??= new CUIAutomation();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint dwFlags, uint dx, uint dy,
        uint dwData, nuint dwExtraInfo);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int GetDlgCtrlID(IntPtr hWnd);

    private const uint BmClick = 0x00F5;
    private const uint WmCommand = 0x0111;
    private const int BnClicked = 0;

    private const uint MouseLeftDown = 0x0002;
    private const uint MouseLeftUp = 0x0004;

    // 桌面会话存在性探针：UserInteractive 之外再确认 UIA 真能枚举到顶层窗口。
    public static bool IsInteractive()
    {
        if (!OperatingSystem.IsWindows() || !Environment.UserInteractive)
        {
            return false;
        }
        try
        {
            return Automation.GetRootElement()
                .FindAll(TreeScope.TreeScope_Children,
                    Automation.CreateTrueCondition()).Length > 0;
        }
        catch
        {
            return false;
        }
    }

    // 进程的全部顶层窗口（NSIS 安装器/卸载器、msiexec 客户端可能同时持有
    // 模态预选页与向导帧——语言预选页在向导创建前弹出）。
    public static IUIAutomationElement[] TopWindowsByProcess(int processId)
        => TopWindows(UIA_PropertyIds.UIA_ProcessIdPropertyId, processId);

    // 窗口类名兜底（MSI 向导帧类名 MsiDialogCloseClass，UI 宿主进程不一定等于启动 pid）。
    public static IUIAutomationElement[] TopWindowsByClass(string className)
        => TopWindows(UIA_PropertyIds.UIA_ClassNamePropertyId, className);

    private static IUIAutomationElement[] TopWindows(int propertyId, object value)
    {
        try
        {
            var found = Automation.GetRootElement().FindAll(TreeScope.TreeScope_Children,
                Automation.CreatePropertyCondition(propertyId, value));
            return Elements(found);
        }
        catch
        {
            return [];
        }
    }

    private static IUIAutomationElement[] Elements(IUIAutomationElementArray? found)
    {
        if (found is null)
        {
            return [];
        }
        var list = new List<IUIAutomationElement>(found.Length);
        for (var i = 0; i < found.Length; i++)
        {
            try
            {
                list.Add(found.GetElement(i));
            }
            catch
            {
                // 元素在枚举瞬间消失（翻页竞态）：跳过。
            }
        }
        return [.. list];
    }

    // 翻页瞬间 FindAll 可能返回 null 或抛 COM 异常：一律视为空。
    private static IUIAutomationElement[] Descendants(
        IUIAutomationElement scope, int controlTypeId)
    {
        try
        {
            return Elements(scope.FindAll(TreeScope.TreeScope_Descendants,
                Automation.CreatePropertyCondition(
                    UIA_PropertyIds.UIA_ControlTypePropertyId, controlTypeId)));
        }
        catch
        {
            return [];
        }
    }

    private static IUIAutomationElement[] Buttons(IUIAutomationElement scope)
        => Descendants(scope, UIA_ControlTypeIds.UIA_ButtonControlTypeId);

    private static IUIAutomationElement[] CheckBoxes(IUIAutomationElement scope)
        => Descendants(scope, UIA_ControlTypeIds.UIA_CheckBoxControlTypeId);

    // NSIS 自绘控件（语言预选页 OK/Cancel）在 UIA 下暴露为 Pane 而非 Button。
    private static IUIAutomationElement[] Panes(IUIAutomationElement scope)
        => Descendants(scope, UIA_ControlTypeIds.UIA_PaneControlTypeId);

    private static void Press(IUIAutomationElement button)
    {
        if (button.GetCurrentPattern(UIA_PatternIds.UIA_InvokePatternId)
            is IUIAutomationInvokePattern invoke)
        {
            invoke.Invoke();
            return;
        }
        if (button.GetCurrentPattern(UIA_PatternIds.UIA_LegacyIAccessiblePatternId)
            is IUIAutomationLegacyIAccessiblePattern legacy)
        {
            legacy.DoDefaultAction();
            return;
        }
        throw new InvalidOperationException($"按钮无可用激活模式：{button.CurrentName}");
    }

    // nsDialogs 等自绘页的按钮对 UIA Invoke 免疫（返回成功但零响应）——
    // 升级路径：真实鼠标注入点击控件中心点（等价人手点击，任何页都生效）。
    private static void PhysicalPress(IUIAutomationElement host, IUIAutomationElement button)
    {
        var rect = button.CurrentBoundingRectangle;
        var x = (rect.left + rect.right) / 2;
        var y = (rect.top + rect.bottom) / 2;
        try
        {
            SetForegroundWindow(new IntPtr(host.CurrentNativeWindowHandle));
            Thread.Sleep(200);
        }
        catch
        {
            // 置前台失败仍点击——坐标命中即可。
        }
        SetCursorPos(x, y);
        Thread.Sleep(100);
        mouse_event(MouseLeftDown, 0, 0, 0, 0);
        mouse_event(MouseLeftUp, 0, 0, 0, 0);
    }

    // 消息直投终级：BM_CLICK 投给按钮 HWND（按钮自行向父窗报 BN_CLICKED），
    // 同时向宿主对话框补投 WM_COMMAND ——两条路任一到达即推进。
    private static void MessagePress(IUIAutomationElement host, IUIAutomationElement button)
    {
        var bh = new IntPtr(button.CurrentNativeWindowHandle);
        var hh = new IntPtr(host.CurrentNativeWindowHandle);
        if (bh != IntPtr.Zero)
        {
            SendMessageW(bh, BmClick, IntPtr.Zero, IntPtr.Zero);
            var id = GetDlgCtrlID(bh);
            if (hh != IntPtr.Zero && id != 0)
            {
                SendMessageW(hh, WmCommand, new IntPtr(id | (BnClicked << 16)), bh);
            }
        }
    }

    private static void SetChecked(IUIAutomationElement box, bool wanted)
    {
        if (box.GetCurrentPattern(UIA_PatternIds.UIA_TogglePatternId)
            is IUIAutomationTogglePattern toggle)
        {
            if ((toggle.CurrentToggleState == ToggleState.ToggleState_On) != wanted)
            {
                toggle.Toggle();
            }
            return;
        }
        // Win32 复选框退化为 LegacyIAccessible 默认动作翻转：STATE_SYSTEM_CHECKED(0x10)
        // 读出当前态，只在与目标不符时才翻转——勾选/取消两侧都生效。
        if (box.GetCurrentPattern(UIA_PatternIds.UIA_LegacyIAccessiblePatternId)
                is IUIAutomationLegacyIAccessiblePattern legacy)
        {
            var isChecked = (legacy.CurrentState & 0x10) != 0;
            if (isChecked != wanted)
            {
                legacy.DoDefaultAction();
            }
        }
    }

    public sealed record Drive(IReadOnlyList<string> Actions, bool Finished);

    // 逐页驱动标准向导直至点中 Finish 或目标进程退出；返回按序点击过的按钮名供断言。
    // 每轮重枚举进程全部顶层窗口，取第一个含候选按钮的窗口（模态预选页优先被驱动）。
    // 候选取每页第一个存在的主按钮：Finish→Install→Remove→Uninstall→Agree→Next→Repair→OK。
    // "OK" 只在 Pane 层匹配（NSIS 语言预选页的 OK 是自绘 Pane，普通页无 Pane 名 "OK"）。
    // autoCheck（MSI 流）：点推进类按钮前补齐未勾复选框（LicenseAgreement 的 Next 需先勾
    // "I accept"）；点 Finish 前清空复选框（ExitDialog 的"启动应用"不带出测试外进程）。
    public static Drive DriveWizard(Func<IUIAutomationElement[]> getWindows,
        Func<bool> done, TimeSpan timeout, bool autoCheck = false)
    {
        var actions = new List<string>();
        var deadline = DateTime.UtcNow + timeout;
        var finished = false;
        var samePagePresses = 0;
        string? lastSig = null;
        // NSIS 语言预选页：不主动选 English 时 LangDLL 默认取系统 UI 语言，
        // zh-CN 宿主上向导全程中文按钮、英文候选名永远匹配不到——确定性卡死。
        // 记录已处理过的对话框句柄，每框只选一次。
        var langHandled = new HashSet<IntPtr>();
        string[] order =
            ["Finish", "Close", "Install", "Remove", "Uninstall", "Agree", "Next", "Repair", "OK"];
        // MessageBox 按钮随 OS 区域设置本地化（是/否/取消），与向导语言无关；
        // 只答推进向选项（Yes/OK 族），拒绝族不代答——没命中就诚实超时。
        string[] modalOrder =
            ["Yes", "是", "&Y", "OK", "确定", "I Agree", "同意"];
        while (DateTime.UtcNow < deadline && !done())
        {
            IUIAutomationElement? pick = null;
            IUIAutomationElement? host = null;
            var isLangDialog = false;
            var windows = getWindows();
            // 模态 MessageBox 会禁掉向导帧——先驱动还 enabled 的顶层窗，
            // 否则在禁用帧上点按钮是永远不生效的死循环（SameVersionDetected 等提示）。
            foreach (var window in windows.OrderByDescending(w => SafeIsEnabled(w)))
            {
                try
                {
                    foreach (var name in order)
                    {
                        // 实体控件过滤：标题栏伪按钮（关窗 X 等）同为 Button 控件类型但类名为空，
                        // 真实按钮类名恒为 "Button"——空类名不误候选（宿主实测 r28）。
                        pick = Buttons(window).FirstOrDefault(b =>
                            HasClass(b) && NameContains(b, name));
                        if (pick is not null)
                        {
                            break;
                        }
                    }
                    pick ??= Panes(window).FirstOrDefault(p => NameContains(p, "OK"));
                    // 模态提示框：英文向导之外的本地化 MessageBox（是/否/确定）。
                    pick ??= Buttons(window).FirstOrDefault(b =>
                        modalOrder.Any(m => NameContains(b, m)));
                }
                catch
                {
                    pick = null;
                }
                if (pick is not null)
                {
                    host = window;
                    break;
                }
            }
            if (pick is null || host is null)
            {
                Thread.Sleep(250);
                continue;
            }
            // 语言预选页判定不看控件形态（OK 可能是真 Button 或自绘 Pane），
            // 看"OK 候选 + 窗口含 ComboBox"的组合——普通向导页无 ComboBox。
            isLangDialog = NameContains(pick, "OK")
                && Descendants(host, UIA_ControlTypeIds.UIA_ComboBoxControlTypeId).Any();
            if (isLangDialog && langHandled.Add(host.CurrentNativeWindowHandle))
            {
                SelectComboItem(host, "English");
            }
            var label = SafeName(pick) ?? "?";
            // 终态按钮：Finish（安装器完成页）与 Close（卸载器完成页等价收尾件）。
            var isTerminal = label.Contains("Finish", StringComparison.OrdinalIgnoreCase)
                || label.Contains("Close", StringComparison.OrdinalIgnoreCase);
            if (isTerminal)
            {
                // 一律清空复选框：NSIS/MSI Finish 页"运行应用"默认勾选会带出测试外进程。
                try
                {
                    foreach (var box in CheckBoxes(host))
                    {
                        SetChecked(box, false);
                    }
                }
                catch
                {
                    // 元素过期——下轮重取。
                }
            }
            else if (autoCheck)
            {
                try
                {
                    foreach (var box in CheckBoxes(host))
                    {
                        SetChecked(box, true);
                    }
                }
                catch
                {
                    // 元素过期——下轮重取。
                }
            }
            if (!IsEnabled(pick))
            {
                // License 页 Next 未勾前置复选框时禁用：勾完下轮再点。
                Thread.Sleep(250);
                continue;
            }
            // 页面指纹：同一窗口句柄+同名按钮连击说明 Invoke 未生效——
            // 升级到物理鼠标注入（nsDialogs 自绘页免疫 Invoke，终态 Finish 同病）。
            var sig = $"{host.CurrentNativeWindowHandle}|{label}";
            if (sig == lastSig)
            {
                samePagePresses++;
            }
            else
            {
                samePagePresses = 0;
                lastSig = sig;
            }
            try
            {
                if (samePagePresses >= 4)
                {
                    // 第三级：消息直投。物理点击会被常驻遮罩层/IME 候选窗吃掉
                    // （坐标命中的是遮罩而非按钮）；BM_CLICK+WM_COMMAND 走消息队列必达。
                    MessagePress(host, pick);
                }
                else if (samePagePresses >= 2)
                {
                    PhysicalPress(host, pick);
                }
                else
                {
                    Press(pick);
                }
            }
            catch
            {
                // 元素过期/无激活模式——下轮重取。
                Thread.Sleep(300);
                continue;
            }
            actions.Add(label);
            // 终态按钮不 break：靠 done() 收敛；Invoke 免疫时同指纹重试会升级物理点击。
            finished |= isTerminal;
            // 页面切换与 InstFiles 段需要窗口一点时间刷新。
            Thread.Sleep(500);
        }
        return new Drive(actions, finished || done());
    }

    // LangDLL 预选页 ComboBox：展开后按项名选中（Win32 组合框在 UIA 下暴露
    // ExpandCollapse + SelectionItem 两套模式；任一不可用即放弃让 OK 带默认语言走）。
    private static void SelectComboItem(IUIAutomationElement host, string itemName)
    {
        try
        {
            var combo = Descendants(host, UIA_ControlTypeIds.UIA_ComboBoxControlTypeId)
                .FirstOrDefault();
            if (combo is null)
            {
                return;
            }
            var expand = combo.GetCurrentPattern(UIA_PatternIds.UIA_ExpandCollapsePatternId)
                as IUIAutomationExpandCollapsePattern;
            expand?.Expand();
            Thread.Sleep(200);
            var item = Descendants(combo, UIA_ControlTypeIds.UIA_ListItemControlTypeId)
                .FirstOrDefault(e => NameContains(e, itemName));
            if (item?.GetCurrentPattern(UIA_PatternIds.UIA_SelectionItemPatternId)
                    is IUIAutomationSelectionItemPattern sel)
            {
                sel.Select();
                Thread.Sleep(150);
            }
            expand?.Collapse();
        }
        catch
        {
            // 选不中就用预选默认——同名腿在英文宿主不受影响，其他宿主向导照常推进。
        }
    }

    private static bool NameContains(IUIAutomationElement el, string name)
        => SafeName(el)?.Contains(name, StringComparison.OrdinalIgnoreCase) == true;

    private static bool SafeIsEnabled(IUIAutomationElement el)
    {
        try
        {
            return el.CurrentIsEnabled != 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool HasClass(IUIAutomationElement el)
    {
        try
        {
            return el.CurrentClassName is { Length: > 0 };
        }
        catch
        {
            return false;
        }
    }

    private static bool IsEnabled(IUIAutomationElement el)
    {
        try
        {
            return el.CurrentIsEnabled != 0;
        }
        catch
        {
            return false;
        }
    }

    private static string? SafeName(IUIAutomationElement el)
    {
        try
        {
            return el.CurrentName;
        }
        catch
        {
            return null;
        }
    }
}
