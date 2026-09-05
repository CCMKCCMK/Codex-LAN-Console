# Scooter 续航记录与返程提醒

入口：底栏“通勤” → “Scooter 续航实验”，深链为
`/commute/?panel=scooter`。充电地点可独立设置。当前路线供应商主要适用
UCSD / San Diego 区域。

## 使用流程

1. 安装 Android 1.9.0，配对电脑，允许精准位置与通知。
2. 正常充满后点“已充满”，设置参考续航、骑手加车总重、充电地点和安全余量。
3. 出发点“开始骑行”，停车后点“停止骑行”。同一次充电可包含多段骑行。
4. 自然遇到低电量无法继续使用时，停车后标记“已没电”；不要为实验强行骑到断电。
5. 提前充电也可点“已充满”，历史保留，但不完整周期不参与容量标定。
6. 定位被系统结束或权限拒绝后，点“恢复定位 / 同步记录”。

Android 开始骑行后有持续系统通知，可从通知停止定位，不会开机偷偷定位。
系统强制停止、收回权限和厂商省电限制仍可能中断记录与提醒。
普通网页只有 HTTPS 下前台定位；HTTP IP 地址和旧 APK 可手动计时、补充里程，
不具备新原生后台记录能力。iOS 未实现后台骑行定位。

## 模型与限制

- GPS 按序号幂等接收，排除低精度点、跳点和中断区间，不用直线补缺失里程。
- 上坡按总重和势能折算额外消耗；下坡最多降低部分基线消耗，不假设车辆回充。
- 地形来自 Open-Meteo / Copernicus GLO-90（约 90 m 网格），不是路面级测量。
- 容量使用最近最多 20 个合格“充满到用尽”周期的等效里程中位数。
  定位缺失、手动里程或地形覆盖不足的周期不参与完整容量标定。
- 少于 3 个有效周期显示“未充分标定”；至少 4 个才展示按时间顺序的历史预测误差。
  两三周不保证特定准确度，温度、胎压、载重、速度和老化仍会影响结果。
- 电量百分比是估算，**不是车载 BMS 读数**。
- 返程结合真实骑行路线、地形与安全余量。缺少新定位或可靠路线时不承诺“够用”。
- 提醒默认 60 秒，可设置 15–3600 秒；系统与网络不保证秒级交付。
  断网可暂存定位，但不能得到电脑端实时返程判断。

## 数据和隐私

电脑：`%LOCALAPPDATA%\CodexLanConsole\Scooter\`，包含
`state.json`、上次保存的 `.bak`、各段 `.jsonl` 定位记录。
手机队列位于 App 私有目录，上限 20,000 点，绑定原配对电脑。
配对凭证沿用 Android Keystore 加密。数据不上传 GitHub。
导出的 JSON 包含充电地点及最后定位，分享前请脱敏。

开启地形会向 Open-Meteo 发送坐标；返程起终点发送给 UCSD Wayfinder。
CARTO / OpenStreetMap 提供地图瓦片；这不是完全离线功能。
可在设置关闭地形与提醒；无地形时不能完成地形校准。
清除数据前先导出、停止骑行与 Bridge，再备份并移除 Scooter 数据目录。
卸载 Android 会删除离线队列，卸载前先联网同步。

供应商依据：[Open-Meteo](https://open-meteo.com/en/docs/elevation-api)、
[Android 定位权限](https://developer.android.com/develop/sensors-and-location/location/permissions)、
[Web 定位](https://developer.mozilla.org/en-US/docs/Web/API/Geolocation/watchPosition)。
Open-Meteo 免费接口供非商业使用；商业部署需安排合适授权或供应商。

## 认证 API

| 方法 | 路径 | 用途 |
|---|---|---|
| GET | `/api/commute/scooter` | 统计、估计、历史 |
| POST | `/api/commute/scooter/action` | full/start/stop/empty，UUID requestId 去重 |
| POST | `/api/commute/scooter/points` | rideId + 最多 100 点，seq 去重 |
| PUT | `/api/commute/scooter/settings` | revision 并发控制 |
| GET | `/api/commute/scooter/export` | 私人统计导出 |

均沿用 Bridge 配对认证；只读查询不启动定位。
