<img src="assets/RouteDeck-256.png" width="96" alt="RouteDeck 图标" />

# RouteDeck

Windows 路由查看、诊断与管理工具。适用于多网卡、公司内网和 VPN 并存时，查清目标连接走哪张网卡、是否经过代理，以及连接失败在哪一层。

## 下载与安装

从 [Releases](https://github.com/linnjoy/RouteDeck/releases/latest) 下载 `RouteDeck-0.1.0-win-x64.zip`。

支持 Windows 10 1809 及以上版本、Windows 11，x64 架构。发布包自带 .NET 运行时，无需另装 SDK。

1. 将 ZIP 完整解压到本地目录。
2. 双击 `Install-RouteDeck.cmd`，在管理员权限提示中确认。
3. 从开始菜单或桌面快捷方式启动 RouteDeck。

安装程序将应用与后台服务安装到 `C:\Program Files\RouteDeck`。界面以普通用户权限运行，路由修改由后台服务执行；安装过程本身不修改路由。

升级时退出界面，解压新版本并再次运行安装脚本。卸载可通过 Windows“设置 → 应用”完成。卸载不会自动撤销已添加的路由，需要先在应用的“操作历史”中撤销。

## 功能

- **路由表**：按网卡分组查看 IPv4/IPv6 路由、网关、有效跃点及路由来源。
- **路径查询**：解释目标 IP 的系统选路，并与 Windows 的实际路由选择进行核对。
- **代理识别**：读取系统代理、PAC、WinHTTP 和代理环境变量，辅助区分系统选路与应用代理路径。
- **分层测试**：分别检查路由、网关、TCP 和 HTTP，支持直连与经代理的对照。
- **路由管理**：预览后添加或修改路由，为指定目标选择物理网卡和网关。
- **操作历史与撤销**：记录修改；撤销时检查当前值，保留之后被其他程序或用户修改的项目。
- **恢复保护**：影响公网出口的操作需要限时确认，超时执行回滚；服务启动时检查未完成的操作。

## 使用

在顶部查询框输入目标 IP 或 `IP:端口`，先查看系统路由路径，再运行连通性测试。

需要让指定目标通过公司有线网络访问时，选择“为这个目标添加直连路由”，核对目标地址、出口网卡和网关，再预览并应用。跨网段目标通常需要填写能到达目标的网关；只有目标确实在本地链路上时才使用直连路由。

## 当前限制

当前版本为 `0.1.0`，属于早期版本。尚未实现网络场景一键切换、托盘及自动规则。服务不可用时，界面仍可查看和诊断，写操作会被禁用。

代理识别用于辅助诊断，不能完整模拟所有应用自身的网络行为。回滚机制也不能替代网络变更前的人工核对。

发布包未进行代码签名，Windows 可能提示未知发布者。目前没有自动更新功能。

## 数据位置

| 位置 | 内容 |
|---|---|
| `%ProgramData%\RouteDeck\history.db` | 操作历史与恢复记录 |
| `%ProgramData%\RouteDeck\logs\` | 后台服务日志 |
| `%LocalAppData%\RouteDeck\ui.json` | 界面偏好 |

## 从源码构建

需要 Windows 和 .NET 8 SDK。

```powershell
dotnet build RouteDeck.sln
dotnet test tests/RouteDeck.Tests/RouteDeck.Tests.csproj --filter "Category!=Admin"
```

需要管理员权限的集成测试会真实读写测试路由，普通构建无需运行。项目包括 WPF 界面、Windows 服务、核心网络逻辑及命名管道协议。

生成与 Releases 对应的 ZIP 安装包：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File build/publish.ps1
```

输出位于 `artifacts/RouteDeck-0.1.0-win-x64.zip`。请将界面与服务保留在同一安装目录。

## 许可证

RouteDeck 的原创代码及资源采用 [PolyForm Noncommercial License 1.0.0](LICENSE)，
允许许可证规定的非商业使用、修改和分发。商业用途须事先另行获得作者授权，可通过 GitHub 联系作者。

本项目属于源码可见的非商业许可项目，不属于允许商业使用的开源许可项目。
分发时请保留许可证及 [版权声明](NOTICE)。第三方组件仍遵循各自的许可证。

## 反馈

通过 [Issues](https://github.com/linnjoy/RouteDeck/issues) 反馈问题。附上 Windows 版本、RouteDeck 版本和复现步骤；提交截图或日志前，请遮盖内网地址、主机名及其他私人信息。
