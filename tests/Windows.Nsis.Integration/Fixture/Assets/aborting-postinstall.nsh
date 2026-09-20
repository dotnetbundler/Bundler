; 直接终止测试安装器，模拟无法执行 .onInstFailed 的进程崩溃。
!macro NSIS_HOOK_POSTINSTALL
  System::Call 'kernel32::GetCurrentProcess() p .r0'
  System::Call 'kernel32::TerminateProcess(p r0, i 2)'
!macroend
