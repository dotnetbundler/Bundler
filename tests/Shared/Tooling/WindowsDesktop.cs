// Windows GUI 验收基建：UI Automation（UIA3 COM）薄封装驱动标准向导
// （NSIS #32770 向导帧、MSI MsiDialog* 对话框）。
// 只在交互式桌面会话可用（explorer 在跑、UIA 可达顶层窗口）；各腿以 Assert.SkipWhen 自行门禁。
using Interop.UIAutomationClient;

internal static class WindowsDesktop
{
    private static IUIAutomation? _automation;
    private static IUIAutomation Automation => _automation ??= new CUIAutomation();

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

    // 按进程号找顶层窗口（NSIS 安装器/卸载器、msiexec 客户端的向导帧）。
    public static IUIAutomationElement? TopWindowByProcess(int processId)
    {
        try
        {
            return Automation.GetRootElement().FindFirst(TreeScope.TreeScope_Children,
                Automation.CreatePropertyCondition(
                    UIA_PropertyIds.UIA_ProcessIdPropertyId, processId));
        }
        catch
        {
            return null;
        }
    }

    // 按窗口类名兜底（MSI 向导帧类名 MsiDialogCloseClass，UI 宿主进程不一定等于启动 pid）。
    public static IUIAutomationElement? TopWindowByClass(string className)
    {
        try
        {
            return Automation.GetRootElement().FindFirst(TreeScope.TreeScope_Children,
                Automation.CreatePropertyCondition(
                    UIA_PropertyIds.UIA_ClassNamePropertyId, className));
        }
        catch
        {
            return null;
        }
    }

    private static IEnumerable<IUIAutomationElement> Descendants(
        IUIAutomationElement scope, int controlTypeId)
    {
        var found = scope.FindAll(TreeScope.TreeScope_Descendants,
            Automation.CreatePropertyCondition(
                UIA_PropertyIds.UIA_ControlTypePropertyId, controlTypeId));
        for (var i = 0; i < found.Length; i++)
        {
            yield return found.GetElement(i);
        }
    }

    private static IEnumerable<IUIAutomationElement> Buttons(IUIAutomationElement scope)
        => Descendants(scope, UIA_ControlTypeIds.UIA_ButtonControlTypeId);

    private static IEnumerable<IUIAutomationElement> CheckBoxes(IUIAutomationElement scope)
        => Descendants(scope, UIA_ControlTypeIds.UIA_CheckBoxControlTypeId);

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
        // Win32 复选框退化为 LegacyIAccessible 默认动作翻转。
        if (wanted &&
            box.GetCurrentPattern(UIA_PatternIds.UIA_LegacyIAccessiblePatternId)
                is IUIAutomationLegacyIAccessiblePattern legacy)
        {
            legacy.DoDefaultAction();
        }
    }

    public sealed record Drive(IReadOnlyList<string> Actions, bool Finished);

    // 逐页驱动标准向导直至点中 Finish 或目标进程退出；返回按序点击过的按钮名供断言。
    // 候选取每页第一个存在的主按钮：Finish→Install→Remove→Uninstall→Agree→Next→Repair。
    // autoCheck（MSI 流）：点推进类按钮前补齐未勾复选框（LicenseAgreement 的 Next 需先勾
    // "I accept"）；点 Finish 前清空复选框（ExitDialog 的"启动应用"不带出测试外进程）。
    public static Drive DriveWizard(Func<IUIAutomationElement?> getWindow, Func<bool> done,
        TimeSpan timeout, bool autoCheck = false)
    {
        var actions = new List<string>();
        var deadline = DateTime.UtcNow + timeout;
        var finished = false;
        string[] order =
            ["Finish", "Install", "Remove", "Uninstall", "Agree", "Next", "Repair"];
        while (DateTime.UtcNow < deadline && !done())
        {
            var window = getWindow();
            if (window is null)
            {
                Thread.Sleep(250);
                continue;
            }
            IUIAutomationElement? pick = null;
            foreach (var name in order)
            {
                pick = Buttons(window).FirstOrDefault(b =>
                    b.CurrentName.Contains(name, StringComparison.OrdinalIgnoreCase));
                if (pick is not null)
                {
                    break;
                }
            }
            if (pick is null)
            {
                Thread.Sleep(250);
                continue;
            }
            var label = pick.CurrentName;
            if (label.Contains("Finish", StringComparison.OrdinalIgnoreCase))
            {
                if (autoCheck)
                {
                    foreach (var box in CheckBoxes(window))
                    {
                        SetChecked(box, false);
                    }
                }
                Press(pick);
                actions.Add(label);
                finished = true;
                break;
            }
            if (autoCheck)
            {
                foreach (var box in CheckBoxes(window))
                {
                    SetChecked(box, true);
                }
            }
            if (pick.CurrentIsEnabled == 0)
            {
                // License 页 Next 未勾前置复选框时禁用：勾完下轮再点。
                Thread.Sleep(250);
                continue;
            }
            Press(pick);
            actions.Add(label);
            // 页面切换与 InstFiles 段需要窗口一点时间刷新。
            Thread.Sleep(500);
        }
        return new Drive(actions, finished || done());
    }
}
