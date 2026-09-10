# WriteME 工程决策工具

本目录保留 WriteME 正在使用的 `write-notes-like-deepseek` 技能与校验工具。上游项目：[czm15053/write-notes-like-deepseek](https://github.com/czm15053/write-notes-like-deepseek)。

- [SKILL.md](SKILL.md)：决定何时记录、原地更新或归档工程决策。
- `references/` 与 `templates/`：分类、格式、质量约定及模板。
- `scripts/`：笔记树与格式校验、归档和看板生成。
- `assets/agent-notes-board.html`：本项目 `npm run init-board` 使用的看板模板。

在仓库根目录运行：

```sh
npm run verify-notes
npm run init-board
```

项目决策放在 `.agents/notes/`。生成的根目录 `board.html` 和上游技能的宣传示例 PNG/SVG 不进入 Git；校验工具无需这些图片。Node 依赖由根目录 `npm ci` 还原。
