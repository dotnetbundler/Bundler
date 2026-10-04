!macro NSIS_HOOK_POSTINSTALL
  ; 验证成功安装的重启退出码、事务提交与禁止立即启动行为。
  SetRebootFlag true
!macroend
