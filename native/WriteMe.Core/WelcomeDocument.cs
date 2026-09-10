namespace WriteMe.Core;

public static class WelcomeDocument
{
    public static NoteNode Create()
    {
        var bold = new NoteNode("text") { Text = "把想法写下来，让思路慢慢展开。", Marks = [new("bold")] };
        var intro = NoteNode.Paragraph() with { Content = [bold] };
        var tip = NoteNode.Paragraph() with
        {
            Content = [new("text") { Text = "选中文字试试 " }, new("text") { Text = "粗体、链接与高亮", Marks = [NoteMark.With("highlight", "color", "#FFF0A8")] }, new("text") { Text = "，或输入 / 查找块类型。" }]
        };
        return new("doc")
        {
            Content =
            [
                intro,
                NoteNode.Paragraph("这里是你的本地笔记空间。文字自动保存，断网也可以继续写。"),
                NoteNode.Paragraph(),
                NoteNode.Paragraph("从一条思路开始") with { Type = "heading", Attrs = NoteNode.Paragraph().WithAttr("level", 2).Attrs },
                NoteNode.Toggle("项目计划", NoteNode.Paragraph("点击左侧三角收起内容；再次展开，每一级都记得自己的状态。"),
                    NoteNode.Toggle("第一阶段 · 让编辑顺手", NoteNode.Paragraph("标题回车创建子项，Ctrl+Enter 创建同级，Tab 与 Shift+Tab 调整层级。"),
                        NoteNode.Toggle("把细节继续展开", NoteNode.Toggle("第四级想法", NoteNode.Toggle("第五级也可以独立折叠", NoteNode.Paragraph("拖动左侧手柄移动整个块，Ctrl+Z 一起撤销文字与结构操作。"))).WithAttr("collapsed", true)))),
                NoteNode.Toggle("收纳暂时不看的内容", NoteNode.Paragraph("内容仍然保存在文档里，折叠只影响当前显示。"), NoteNode.Paragraph("你可以把其他块拖到标题中间，放进这个折叠块。")).WithAttr("collapsed", true),
                NoteNode.Paragraph(),
                tip,
                new("taskList") { Content = [new("taskItem") { Content = [NoteNode.Paragraph("写下今天最想完成的一件事")] }] },
                NoteNode.Paragraph()
            ]
        };
    }
}
