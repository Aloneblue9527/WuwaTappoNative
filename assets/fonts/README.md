# DS5 面键字体

`WuwaPadSymbols.ttf` 是 [Tabler Icons webfont 3.35.0](https://www.npmjs.com/package/@tabler/icons-webfont/v/3.35.0) 的 outline 子集，MIT 许可见 [Tabler-LICENSE.txt](Tabler-LICENSE.txt)。四个图标来自同一套 24 × 24、2 单位笔画的线性图标；不进行额外加粗。

| 应用字符 | Unicode | Tabler 图标 | 上游码位 |
|---|---|---|---|
| △ | U+25B3 | triangle | U+EB44 |
| ○ | U+25CB | circle | U+EA6B |
| × | U+00D7 | x | U+EB55 |
| □ | U+25A1 | square | U+EB2C |

字体大小为 **1,880 字节**，只有以上四个可映射字符和 TrueType 必需的空 `.notdef` 字形；没有字母、汉字、其它图标或完整上游字体。保留原有字符映射，不修改输入逻辑。

字体以嵌入资源加载到 GDI+ 私有字体集合，无需在系统安装。仅气泡和手柄面板的这四个符号使用 Regular 字重；其它按键仍使用 Microsoft YaHei UI Bold。字体数据在进程存活期间保持固定，避免 GDI+ 引用失效。

重新生成（开发工具依赖，不属于应用运行依赖）：

```sh
python -m pip install fonttools==4.61.1
python tools/subset-pad-symbols.py
```

脚本固定上游版本和 SHA-256，并检查最终只有四个字符、五个字形。完整上游字体仅在内存中处理，不写入仓库或发布目录。

前后示意图及复现方法见 [DS5 字体对比](../../docs/dualsense-font/README.md)。
