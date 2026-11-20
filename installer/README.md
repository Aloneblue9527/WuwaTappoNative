# 打包说明

## 产物

发布采用 Helper 风格的单文件结构：安装目录和绿色版目录都只有一个自包含的 `WuwatappoNative.exe`，素材、网页和运行时由单文件在启动时解压。

- `dist/WuwatappoNative-<版本>-Setup.exe`：Inno Setup 安装包，默认带管理员清单，启动时显示安装模式并默认“为所有用户安装（推荐）”，选择后请求 UAC，以便关闭管理员模式运行的桌宠；也可选择仅当前用户的标准权限安装。安装向导支持在目录页自定义安装位置：仅当前用户模式默认 `%LOCALAPPDATA%\Programs\Wuwatappo Native`，所有用户模式默认 `%ProgramFiles%\Wuwatappo Native`，开始菜单、桌面快捷方式和卸载信息都会跟随实际安装路径与安装模式。
- `dist/WuwatappoNative-<版本>-portable.zip`：解压即用，运行 `WuwatappoNative.exe`。
- `dist/WuwatappoTokenExtractor-1.1.2-x64.zip`：独立的单文件 token 提取器（无需 Python），源码在 `token-extractor/`。

安装包默认带管理员清单，启动时显示安装模式并默认“为所有用户安装（推荐）”，选择后请求 UAC，以便通过 `taskkill` 结束管理员模式运行的桌宠；也可选择仅当前用户的标准权限安装。安装器同时使用 Inno Setup 的 `CloseApplications` 和 Native 单实例 Mutex 检测：如果 Native 正在运行，会提示用户确认关闭；无法关闭时提示用户先从托盘退出。

## 安装向导外观

安装向导使用 `assets/installer/` 中的暖色奶油风图片，由 `installer/generate-wizard-art.ps1` 从仓库自带的角色头帧和 `assets/app.ico` 生成，包含 100%–250% DPI 尺寸，不引用仓库外素材。欢迎页、安装目录页和完成页使用统一的中文 Tappo 文案。

## 依赖

- Windows 10/11 x64
- WebView2 Runtime（Win10/11 一般已内置；缺失时面板无法显示）
- 自包含发布，不要求安装 .NET 8 Runtime
- 打包机需要 Inno Setup 6.7+（`ISCC.exe`）

## 重新打包

```powershell
cd wuwatappoNative
pwsh -File installer/build.ps1 -Version 0.3.5
```

本地测试 beta 时可显式传入三个版本参数：

```powershell
pwsh -File installer/build.ps1 -Version 0.3.5.919 -DisplayVersion 0.3.5-beta.919 -InformationalVersion 0.3.5-beta.919
```

脚本会：单文件 publish → Inno Setup 编译安装包 → 打便携 ZIP → 构建单文件 token 提取器 ZIP。

## 已做的验证

- 单文件 publish 只生成一个 `WuwatappoNative.exe`。
- 使用 `0.3.5-beta.919` 验证：文件版本 `0.3.5.919`，Inno Setup 6.7.3 编译通过，输出 `WuwatappoNative-0.3.5-beta.919-Setup.exe`。
- 安装向导可进入目录页并显示默认路径；管理员编译版本通过 `/CURRENTUSER` 验证仅当前用户模式默认到 `%LOCALAPPDATA%\Programs\Wuwatappo Native`。
- 沙盒安装到带空格的自定义路径，确认开始菜单（命名分组）、桌面快捷方式、卸载注册项和卸载清理均正常。
- 安装包和绿色版均不依赖目标机器安装 .NET 8 Runtime。

## 注意

- `assets/`、`data/appearance_asset.json` 来自本机原件，仅限本机使用，禁止再分发。
- `assets/installer/` 的向导图片是从上述素材生成的安装器专用图，版权约束与 `assets/default` 一致。
- 卸载会尝试清理开机自启的 `HKCU\...\Run` 项和 `WuwatappoNative` 计划任务，但不会删除 `%LOCALAPPDATA%\WuwatappoNative` 下的配置、日志与缓存。
