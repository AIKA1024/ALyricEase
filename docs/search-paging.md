# 搜索页分页(滚到底续页)

用户反馈:搜索结果的「歌曲 / 专辑 / 表演者 / 歌单 / 用户」每个分类只有固定的几十条,
滚到底不会加载新内容。原来单类型 Tab 写死 `TabLimit = 30` 一次拉完,没有任何分页。

---

## 1. 接口层:`SearchAllAsync` 多一个 `offset`

```csharp
Task<SearchAllResult?> SearchAllAsync(string keyword, SearchKind kind, int limit, int offset = 0, CancellationToken ct = default)
```

- **网易云**:`weapi/cloudsearch/get/web` / 明文 `/api/cloudsearch/pc` 本来就有 `offset`,
  但 `TypedSearchAsync` 一直硬编码 0(只有歌曲路径透传)。现在参数化,风控回落路径同样带 offset。
- **QQ**:上游只有 `page_num`(1 起),没有 offset ⇒ `PageOf(offset, limit) = offset / limit + 1`。
  歌单/专辑/歌手/用户四个私有搜索方法原来把 page 写死 1,现在都按 offset 换算。
- **综合模式**:只有歌曲,两源各自用同一个 offset 并发,再 `SongSearchMerger.Merge` 去重合并。
  两源分页各自推进,合并后不足 `limit` 是正常现象,`HasMore` 用"任一源满页"判定。

### QQ 用户通道:不能"不 limit",但也不能截断

`search_type=8` 的 `num_per_page` 行为与其它类型不一样(2026-11-13 实测,关键词「周杰伦」):

| 请求 | 返回 |
|---|---|
| 不传 `num_per_page` | 15 条(服务端默认页大小) |
| `num_per_page=100` | **0 条**(超上限直接空,不是截断) |
| `num_per_page=30` | **55 条** |
| 同上 + `page_num=2` | 30 条,与第 1 页只重叠 3 条 |

结论:
- **"不传 limit"行不通**:默认只给 15 条,比要 30 还少;传大了直接空。
- **"截断到 30"有害**:第 1 页实际覆盖 1~55,第 2 页从第 56 条起。截到 30 会漏掉 31~55。
- 正确做法:**服务端给多少收多少**,而游标仍按**请求条数**推进(`offset += limit`)——
  `page = offset/limit + 1` 每次 +1,窗口恰好接上(实测 55 + 30 = 85 条、重叠 0)。
  按"实际收到条数"推进反而会让 page 跳窗。
- `All` 页只要 5 条:在 VM 的 `AppendResult(result, maxPerSection)` 里截断,不靠接口截断。

## 2. VM 层:`LoadMoreAsync` + 跨页去重

- `_pageOffset`:当前结果已请求到第几条;`LoadTabCoreAsync` 首屏置 `limit`,续页 `+= limit`。
- `HasMore`:首屏/续页**满页**(条数 ≥ 请求条数)才置 true;到底、失败都置 false。没有条数上限——
  行列表是虚拟化的(见 §3),翻多少页都只实化视口附近的行。
- `_seenKeys`:跨页去重(`类型|音源|数字id|mid`,id 为 0 时退回 mid/名字)。QQ 用户通道会超发
  (`limit=30` 回 54 再截断),翻页会重复给个别条目,不去重就会看到重复行。
- 续页失败只 `HasMore = false`,不动 `Message` —— 已有结果不能因为续页失败变成空态。

## 3. 视图层:虚拟化 + 两种触发

单类型 Tab 的 5 个 `ItemsControl`(歌曲/歌单/专辑/表演者/用户)都把 `ItemsPanel` 换成
`VirtualizingStackPanel`,**外层 `ResultsScroll` 负责滚动**(与 `PlaylistView` 同一个模式:
外层 ScrollViewer 直接包裹 ItemsControl + VirtualizingStackPanel,不需要控件自己带 ScrollViewer)。
实测 150 条数据只实化 **11 个 TrackRow**;切走 Tab 再切回也能重新实化(不是 Avalonia #15194)。
`All` 页各分区只有 5 条,保持普通 `StackPanel`。

触发续页有两条路:

1. **触底**:`ResultsScroll.ScrollChanged` 里 `Offset.Y + Viewport.Height >= Extent.Height - 240`
   ⇒ `LoadMoreAsync()`(VM 内部 `IsLoadingMore` 单飞,滚动事件重复触发无副作用)。
2. **不满一屏**:内容比视口矮时用户根本滚不动,触底事件永远不会来 ⇒ `IsSearching/HasMore`
   变化后 Post 一次检查,`Extent.Height <= Viewport.Height` 就继续补页,直到填满或到底。

页脚两行互斥:`加载中…`(`IsLoadingMore`)/`没有更多结果`(`ShowNoMore`,要求 `!HasMore`)。

> 虚拟化面板的 `Extent` 是"平均行高 × 条数"的估算值,触底阈值按估算值工作,实测无提前/滞后。

## 4. 探针与实测

```bash
--search-paging [关键词]      # 两源 × 五种单类型,各拉 p1/p2,报条数与重叠
--search-paging-ui           # 真实 SearchView 滚到底,断言条数增长 / 无重复 / 收尾
```

2026-11-13 实测(关键词「周杰伦」):

| 源 | 类型 | p1 | p2 | 重叠 |
|---|---|---|---|---|
| 网易云 | 歌曲 | 30 | 30 | 0 |
| 网易云 | 专辑 | 60 | 60 | 0(接口按 2×limit 给,offset 仍按 limit 推进) |
| QQ | 歌曲 | 30 | 30 | 0 |
| QQ | 专辑 | 30 | 30 | 0 |
| QQ | 表演者 | 11 | 0 | 0(QQ 只给 11 位,首屏不满页 ⇒ 不翻) |
| QQ | 歌单 | 30 | 29 | 0 |
| QQ | 用户 | **55** | 30 | 3(排序抖动,去重键兜住;合计 85 条无缺口) |

`--search-paging-ui` 结果:歌曲 30 → 滚到底 60 → 90 → 120 → 150,全程无重复,
**150 条数据只实化 11 个 TrackRow**;专辑 30→60、歌单 30→59、用户 30→60,
表演者 11(QQ 只有 11 位,首屏不满页 ⇒ 到底,页脚显示「没有更多结果」);
综合模式 30→60;切走 Tab 再切回歌曲 Tab,30 条数据实化 9 行(虚拟化正常重建)。

> 注意:网易云专辑/歌手等类型单页会返回 **2×limit** 条(接口超发),`IsFullPage` 只判 `>= limit`,
> 不影响分页推进;真正到底靠"返回条数 < limit"。
