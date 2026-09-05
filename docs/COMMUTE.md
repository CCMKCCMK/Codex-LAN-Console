# Codex Console — 私人通勤助手

入口：`/commute/`，也可从 Codex Console 底栏的「通勤」直接进入。主界面与通勤页均使用「任务 / 通勤 / 远程控制 / 设置」导航；行程记录与通勤偏好在通勤页右上方，亦可从 Console 设置打开偏好。与现有 Bridge 共用配对与 Android 后台通知，不增加常驻 Node/Python 服务，也不定时调用付费 AI 模型。

1.8.2 修复：`/commute` 与 `/commute/` 明确路由到通勤页面，不再落入 Console 首页的 SPA fallback。验收同时检查页面特征与资源类型，不以 HTTP 200 作为成功的唯一条件。根 Service Worker 不再拦截通勤路由，避免离线时误显示 Console 首页。

## 首次设置

1. 开源版以公共校园地标作为示例；首次使用请设置自己的出发点和目的地。地点与实际行程只存入本机 AppData，不随源码发布。已有设置不会被示例覆盖。
2. 09:00 到校、18:00 到家只是初始可编辑值。提醒默认关闭，请自行保存实际时间和工作日。
3. 在「车在哪里」标记自有自行车、scooter、汽车的位置。默认均为尚未拥有/不可用。步行与公交默认可以使用。
4. 最多选两种偏好。没有明确偏好时，至少有三次真实行程后再参考使用频率；不会根据使用频率擅自宣称用户拥有某辆车。
5. 在现有 Codex Android APP 内打开，开启后台通知，发送测试通知，并确认系统允许后台运行和发声。普通网页关闭后的定时响铃没有保证。

## 比较方法与边界

- 使用 UCSD 官方 Wayfinder 对外网页接口的 MTS OpenTripPlanner 路网和公交规划结果；包含具体道路几何、换乘和时刻。不是直线距离推算。
- 「现在出发」比较从现在起出门准备、步行、等车、乘车到目的地的总时间。「指定到达」倒推出发时间并保留用户设置的余量；评分也计入过早到达的额外时间，避免为短车程反而提前很久出门。
- 步行默认 4.5 km/h，自行车 15 km/h，scooter 12 km/h，均为用户可调估值，不是 UCSD 官方保证速度。
- 部分上游参数（例如 walkSpeed）不由官方网页代理传递，所以本地按路段距离重新计算步行/骑行时间，检查慢走后还能否赶上公交；不把上游较快的默认步速直接用于推荐。
- 同方向至少三次有效完整行程后，用最近最多 15 次的门到门速度中位数校准；限制校准范围。取消或明显超长的行程不用于学习。
- 已选偏好只影响最多三分钟的评分，不掩盖交通工具不可用。没有偏好时参考最近 20 次记录。
- Scooter 使用自行车路网作为估计，不是 scooter 合法通行认证。人行道/校园禁骑路段需下车推行；不推定存在共享车或电量足够。
- 驾驶没有实时路况、车位数据：另外预留可调停车及步行时间，默认 12 分钟。不是停车导航。
- 公交行程明确标记「时刻表」或「实时预测」。独立到站看板采用 OneBusAway 预测，并检查最后更新时间；超过三分钟的车辆位置不当作实时位置展示。不把末站只下客的到站记录当作可上车班次。
- 未来 14 天内可计划。路线服务会依运营日历计算；官网静态运营说明只作参考。失败或无班次时明确提示，不伪造发车时间。
- 地图来自 OpenStreetMap，显示所选道路路线、上/下车站与新鲜车辆位置；地图不是逐转向导航。

## 提醒与车辆位置

- 使用 America/Los_Angeles 时区（自动处理夏令时）。后台每 30 秒轻量检查，仅接近已设置目标时间时查询路线。
- 提醒持久去重，同日同方向同目标时间只发一次。没有能确认的方案时提醒手动核对，不建议冒险赶车。
- 已开始或当天同方向已完成的行程，不重复提示出发。确实出发时点击开始，到达后点击确认；骑行/驾驶完成会更新该工具的位置，其他工具不会跟着移动。
- 不持续跟踪 GPS。手机断开不会停止 Windows 提醒检查，但离线手机无法及时收到通知。Windows 必须在线且未休眠。
- 通知使用专门的 `commute_departure` 类型和 `commute` 虚拟目标。前端为已安装 Android 的通知路由回执提供兼容入口，不发送这个虚拟 ID 到 Codex app-server。

## 数据与隐私

持久数据在 `%LOCALAPPDATA%\CodexLanConsole\commute.json`：地点、时间、车辆位置、最多 300 次行程。采用原子替换和修订号检查，防止多设备把旧设置覆盖回来。所有 API 受现有配对认证保护。

查询时坐标、时间、地点搜索词会发往 UCSD 官方页面使用的 OneBusAway/路线规划服务；地图瓦片请求会发往 OpenStreetMap。不会发送 Codex 会话、房间号、配对凭据。上游 HTTP 请求域名固定，不能作为任意网址代理。

## 数据源

- UCSD 官方校车入口（已切换 OneBusAway）：https://transportation.ucsd.edu/campus/shuttles/index.html
- Mesa Loop 说明：https://transportation.ucsd.edu/campus/shuttles/mesa-loop.html
- UCSD Wayfinder：https://wayfinder.ucsd.onebusawaycloud.com/
  - `/api/otp/plan`：步行、自行车、驾驶与公交组合规划。
  - `/api/oba/geocode-location`：按名称解析地点。
  - `/api/oba/stops-for-route/{id}`：线路、站点、形状。
  - `/api/oba/trips-for-route/{id}`：运营车辆和位置。
  - `/api/oba/arrivals-and-departures-for-stop/{id}`：计划与预测班次。
- HDSI 官方位置：https://fse.ucsd.edu/_files/Directions%20to%20HDSI%20V1.pdf
- OneBusAway 接口语义：https://developer.onebusaway.org/api/where/methods/arrivals-and-departures-for-stop/
- Leaflet 1.9.4：附带 BSD 2-Clause LICENSE；OSM 底图署名保留。

注意：这是官方网页使用的公开接口，不是 SLA 承诺的第三方商用 API；已设置缓存、并发上限、超时和错误退化。上游路径变更时应更新单独的 CommutePlanner 适配器。

## 维护与验证

后端：`backend/bridge/Commute/`；界面：`frontend/web/commute/`；通知复用现有 NotificationStore。

支持的浏览器上还可注册只读 WebMCP 工具 `read_commute_plan`，供 AI 查询同一份方案，不暗中修改设置或触发通知。本次环境未提供受支持的 WebMCP 执行上下文，因此这一可选入口未做端到端验证；普通页面与 API 不依赖它。

单元/协议测试增加了默认不擅自开启提醒、车辆位置、取消行程、持久化、多设备冲突、太平洋夏令时、步速与到达余量检查。

`scripts/Test-LiveCommute.ps1` 验证真实路线和实时信息，仅查询公共交通数据，不调用模型、不写入行程、不发通知、不修改用户偏好。它会创建一个本机测试配对，令牌仅保存在进程内存中。
