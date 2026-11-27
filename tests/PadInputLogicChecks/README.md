# 扳机输入逻辑回归检查

```sh
dotnet run --project tests/PadInputLogicChecks/PadInputLogicChecks.csproj -c Release
```

仅回放输入状态和 XInput keystroke 数据，不启动应用、不调用 XInput DLL、不需要 Windows 或手柄。
与 `PadInputChecks` 共用扳机测试，检查 LT/RT 状态与事件队列的同轮/跨轮去重、浅按到深按、长按、释放、快速连按、队列短点按、首帧同步及多键释放。

Windows 侧的气泡名称与事件发送检查仍在 `tests/PadInputChecks` 中。
