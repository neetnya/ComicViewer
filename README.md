# ComicViewer

一个基于 WPF（.NET 9）的本地漫画 / 图片查看器。面向「本地漫画库」场景设计：左侧目录导航、右侧连续阅读，便携式单文件发布，无安装、无依赖。

## 功能特性

- **两种查看模式**
  - 漫画模式：整本漫画垂直连续滚动，虚拟化渲染，边看边加载，滚轮惯性滑动。
  - 图片模式：单张查看，左右键翻页，按住右键滚轮临时缩放。
- **左侧目录导航**
  - 启动时显示所有磁盘盘符（`C:\`、`D:\`…）。
  - 单击目录：选中高亮，并尝试阅读该目录（仅读当前目录自身图片，不递归）。
  - 双击目录：进入下一级，左侧显示其子目录。
  - 「返回上一级」按钮回到父目录，并保持刚离开的子目录高亮。
- **末页自动续读**
  - 看到最后一页再点击，自动进入下一个同级文件夹（对应「下一本漫画」）。
  - 进入下一本时，左侧目录同步选中并高亮该目录，但不改变列表层级。
- **模式切换保持当前页**：漫画 ↔ 图片切换时停留在同一张图片。
- **全屏**：`F11` 切换，全屏时顶栏 / 侧栏自动隐藏，鼠标移至边缘呼出；切换全屏仅等比缩放、不重新解码，不闪屏。
- **文件拖放**：把图片文件或文件夹拖入窗口即可打开。
- **便携式配置**：配置存于 exe 同目录 `config.json`。

## 交互说明

### 左侧目录（重点）

| 操作 | 效果 |
| --- | --- |
| 单击目录 | 高亮选中；若该目录自身有图片，右侧开始显示 |
| 双击目录 | 进入该目录，左侧变为其子目录列表 |
| 返回上一级 | 回到父目录，并高亮刚才所在的子目录 |
| 鼠标在侧栏滚动 | 只滚动侧栏列表，不影响右侧图片 |

目录导航**不递归**更深的子目录：单击只读当前目录自己这一层的图片；要深入子目录请双击。

### 右侧阅读区

| 模式 | 左键 | 右键 | 滚轮 |
| --- | --- | --- | --- |
| 漫画模式 | 翻一页（末页→下一本） | （无操作） | 惯性滚动；`Ctrl`+滚轮调宽度 |
| 图片模式 | 下一张（末张→下一本） | 上一张（首张→上一本） | `Ctrl`+滚轮临时缩放 |

### 键盘快捷键

| 按键 | 功能 |
| --- | --- |
| `F11` | 切换全屏 |
| `Esc` | 退出全屏 |
| `←` / `PageUp` | 上一张 / 上一页 |
| `→` / `PageDown` / `空格` | 下一张 / 下一页 |
| `Enter` | 等同鼠标左键（漫画翻一页 / 图片下一张，末页进入下一本） |

## 构建与运行

### 环境要求

- Windows 10 / 11（x64）
- [.NET 9 SDK](https://dotnet.microsoft.com/download)

### 调试运行

```powershell
dotnet run
```

### 发布便携式单文件

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1
```

或手动：

```powershell
dotnet publish -c Publish -r win-x64 --self-contained false -o publish
```

产物在 `publish\ComicViewer.exe`，直接拷贝运行。首次运行后会在 exe 同目录生成 `config.json`。

## 项目结构

```
ComicViewer/
├── App.xaml / App.xaml.cs       # 应用入口
├── MainWindow.xaml / .cs        # 主窗口（两种查看模式、目录导航、全屏等）
├── SettingsWindow.xaml / .cs    # 设置窗口（缩放比例、滚轮距离、启动全屏、文件关联）
├── Models/
│   └── AppConfig.cs             # 配置模型（序列化为 config.json）
├── Services/
│   ├── ConfigService.cs         # 配置读写（exe 同目录 config.json）
│   ├── FolderNavigationService.cs # 目录 / 盘符 / 同级文件夹枚举与自然排序
│   ├── ImageLoaderService.cs    # 异步图片解码（GIF 动画、按宽度缩放）
│   └── FileAssociationService.cs # 注册图片类型关联（出现在系统默认应用列表）
├── VisualTreeExtensions.cs      # 可视化树辅助扩展方法
├── ComicViewer.ico              # 应用图标
├── app.manifest                 # 高 DPI 声明、权限级别
├── build.ps1                    # 一键发布脚本
└── ComicViewer.csproj
```

## 配置说明

`config.json`（自动生成，位于 exe 同目录）：

| 字段 | 含义 | 默认值 |
| --- | --- | --- |
| `Mode` | 查看模式（`Comic` / `Image`） | `Comic` |
| `ComicWidthRatio` | 漫画图片宽度 = 窗口宽度 × 该值（0.1~1.0） | `0.75` |
| `StartFullscreen` | 启动时全屏 | `true` |
| `ScrollStepPixels` | 滚轮每格惯性滚动像素 | `300` |

## 常见问题

- **打开大本漫画稍卡**：首屏只同步解码第一张，其余尺寸与图片在后台渐进加载，滚动到再解码，属预期行为。
- **自动关联图片类型**：设置里的「自动关联图片类型」会写入注册表 ProgId / 应用程序条目 / Capabilities，让 ComicViewer 出现在系统「默认应用」候选列表中；真正设为默认受 Windows `UserChoice` 保护，需在弹出的默认应用页手动选择。
- **图片不显示**：确认目录内图片扩展名在支持列表内（`.jpg` `.jpeg` `.png` `.gif` `.bmp` `.webp` `.tif` `.tiff` `.ico` `.wmf` `.emf`）。
