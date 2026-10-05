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
        string[] order =
            ["Finish", "Install", "Remove", "Uninstall", "Agree", "Next", "Repair", "OK"];
        while (DateTime.UtcNow < deadline && !done())
        {
            IUIAutomationElement? pick = null;
            IUIAutomationElement? host = null;
            foreach (var window in getWindows())
            {
                try
                {
                    foreach (var name in order)
                    {
                        pick = Buttons(window).FirstOrDefault(b =>
                            NameContains(b, name));
                        if (pick is not null)
                        {
                            break;
                        }
                    }
                    pick ??= Panes(window).FirstOrDefault(p => NameContains(p, "OK"));
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
            var label = SafeName(pick) ?? "?";
            if (label.Contains("Finish", StringComparison.OrdinalIgnoreCase))
            {
                if (autoCheck)
                {
                    foreach (var box in CheckBoxes(host))
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
                foreach (var box in CheckBoxes(host))
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

    private static bool NameContains(IUIAutomationElement el, string name)
        => SafeName(el)?.Contains(name, StringComparison.OrdinalIgnoreCase) == true;

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
