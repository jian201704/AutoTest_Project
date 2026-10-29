# AutoTest_Project

OpenTAP 9.34.0 上的自定义自动化项目。业务插件、工作流编译器、脚本指南、WPF 编辑器和辅助工具在本仓库维护；OpenTAP 核心仍由独立源码树或后续发布包提供。

## 内容

- `Plugins/Script`：C# Plan Script 插件、示例和[使用指南](Plugins/Script/使用指南.md)
- `Plugins/Python`：基于官方 OpenTAP Python 插件的 Python TestStep 模块、配置脚本和[使用说明](Plugins/Python/README.md)
- `Plugins/Uds`：UDS 仪器与步骤插件
- `Plugins/Workflow`：Excel 工作流解析与计划编译
- `Plugins/Uds.Tests`：UDS、脚本和工作流测试
- `WpfEditor`：自定义 WPF 测试计划编辑器，可加载 `Plugins/Python` 中的 Python TestStep
- `Jems_UDS`：UDS parser Git 子模块
- `poc`、`Tools`：验证用程序和虚拟 DoIP ECU

## 构建

默认假设本仓库与 `opentap-9.34.0` 同级：

```text
SourceCode/
  AutoTest_Project/
  opentap-9.34.0/
```

在仓库根目录执行：

```powershell
dotnet build .\AutoTest_Project.slnx
```

如果 OpenTAP 源码位于其他位置，可显式传入路径：

```powershell
dotnet build .\AutoTest_Project.slnx -p:OpenTapSourceRoot="E:\SourceCode\opentap-9.34.0"
```

克隆后初始化 UDS 子模块：

```bash
git clone --recurse-submodules https://github.com/jian201704/AutoTest_Project.git
```

更多迁移边界和后续维护方式见[迁移说明](迁移说明.md)。
