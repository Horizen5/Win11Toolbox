# Win11 工具箱 · 本机 API 手册

> 适用版本：**v1.4.1 及以上**（`Win11Toolbox.exe` 完整版、`Win11Toolbox-4.8.1.exe`）
> 接口版本：`v1`　默认地址：`http://127.0.0.1:5180`
> 文字版（`Win11Toolbox-text.exe`）不含 Services 层，**没有** API。

这份 API 是给 **Agent / 脚本 / 其他程序**用的：让它们能直接读到工具箱已经读到的
硬件、传感器、电压、设置状态，并在明确授权后执行少量操作。

设计上只做三件事：**只绑回环地址、必须带 Token、默认只读**。

---

## 1. 快速开始

### 第 1 步：启动服务

```bat
:: 只读模式（推荐先用这个）
Win11Toolbox-4.8.1.exe --api

:: 指定端口
Win11Toolbox-4.8.1.exe --api 5180

:: 允许执行操作（启动工具、调速、改设置）
Win11Toolbox-4.8.1.exe --api --allow-actions
```

启动后**不会出现任何窗口**，进程在后台监听，直到你结束它：

```bat
taskkill /IM Win11Toolbox-4.8.1.exe /F
```

### 第 2 步：取 Token

首次启动会自动生成 32 位 Token 并写在这里：

```
%APPDATA%\Win11Toolbox\api-token.txt
```

完整路径通常是 `C:\Users\<用户名>\AppData\Roaming\Win11Toolbox\api-token.txt`。
**Token 会一直复用**，删掉这个文件才会重新生成。

### 第 3 步：发第一个请求

```bash
TOKEN=$(cat "$APPDATA/Win11Toolbox/api-token.txt")

curl -H "Authorization: Bearer $TOKEN" http://127.0.0.1:5180/api/v1/health
```

```json
{
  "ok": true,
  "name": "Win11 工具箱",
  "version": "1.4.1",
  "apiVersion": "1",
  "port": 5180,
  "url": "http://127.0.0.1:5180/",
  "startedAt": "2026-11-14 16:58:42",
  "uptimeSeconds": 4,
  "elevated": true,
  "pawnIoDriver": true,
  "actionsEnabled": false
}
```

---

## 2. 启动参数

| 参数 | 说明 |
| --- | --- |
| `--api` | 启动 API 服务，不开窗口。默认端口 **5180** |
| `--api <端口>` | 换端口，取值 1–65535。端口被占用时进程退出码 **3** |
| `--allow-actions` | 开放写操作（调速、启动工具、改设置）。不加则写端点一律 403 |
| `--token <字符串>` | 临时指定 Token，不读写 `api-token.txt`。适合自动化测试 |

示例：

```bat
Win11Toolbox-4.8.1.exe --api 8080 --allow-actions
Win11Toolbox-4.8.1.exe --api --token 0123456789abcdef0123456789abcdef
```

### 服务写出的状态文件

`%APPDATA%\Win11Toolbox\` 下：

| 文件 | 内容 |
| --- | --- |
| `api-token.txt` | 32 位十六进制 Token，无 BOM |
| `api-runtime.json` | `port` / `url` / `pid` / `startedAt` / `actionsEnabled` / `tokenFile` |
| `api.log` | 启动信息与每条请求的一行记录（超过 1 MB 会滚到 `api.log.old`） |

`api-runtime.json` 让 Agent **不用猜端口**：

```python
import json, os, urllib.request

home = os.path.expandvars(r"%APPDATA%\Win11Toolbox")
runtime = json.load(open(os.path.join(home, "api-runtime.json"), encoding="utf-8"))
token = open(os.path.join(home, "api-token.txt"), encoding="utf-8").read().strip()

req = urllib.request.Request(
    runtime["url"] + "api/v1/health",
    headers={"Authorization": "Bearer " + token})
print(urllib.request.urlopen(req).read().decode("utf-8"))
```

> 进程被 `taskkill /F` 强杀时来不及清理，`api-runtime.json` 会残留。
> 以 `GET /api/v1/health` 是否通作为唯一的存活判据。

---

## 3. 鉴权

除下面两个端点外，所有接口都需要 Token：

- `GET /`、`GET /api`、`GET /index.json`：端点清单，不含任何机器数据
- `GET /openapi.json`：接口描述

Token 有三种传法，任选其一：

```http
Authorization: Bearer <token>
```
```http
X-Api-Token: <token>
```
```
?token=<token>
```

推荐第一种。第三种会把 Token 写进访问日志，只作为临时测试手段。

Token 不对或缺失返回 **401**：

```json
{
  "ok": false,
  "error": "缺少或错误的 Token。请带请求头 Authorization: Bearer <token>，Token 在 C:\\Users\\...\\api-token.txt"
}
```

---

## 4. 端点总表

| 方法 | 路径 | 作用 | 需要 `--allow-actions` |
| --- | --- | --- | --- |
| GET | `/` | 端点索引 | 否 |
| GET | `/openapi.json` | OpenAPI 3.0 描述 | 否 |
| GET | `/api/v1/health` | 服务状态、版本、权限、驱动 | 否 |
| GET | `/api/v1/system` | CPU / 内存 / 进程数 / 开机时长 | 否 |
| GET | `/api/v1/hardware` | CPU / 主板 / 内存条 / 显卡 / 硬盘 / 分区 / 网卡 | 否 |
| GET | `/api/v1/sensors` | 风扇转速、温度 | 否 |
| GET | `/api/v1/voltages` | 三路主轨与其他电压（含压降） | 否 |
| GET | `/api/v1/snapshot` | system + sensors + voltages 一次取全 | 否 |
| GET | `/api/v1/tools` | 47 项系统工具清单 | 否 |
| GET | `/api/v1/settings` | 65 项轻松设置及当前状态 | 否 |
| GET | `/api/v1/tray` | 托盘图标清单与位置 | 否 |
| GET | `/api/v1/apps` | 已安装应用（较慢，缓存 5 分钟） | 否 |
| POST | `/api/v1/tools/launch` | 启动一个工具 | **是** |
| POST | `/api/v1/fan/set` | 设置风扇百分比 | **是** |
| POST | `/api/v1/fan/auto` | 风扇交回 BIOS 控制 | **是** |
| POST | `/api/v1/settings/set` | 修改一项设置 | **是** |

---

## 5. 只读端点

### 5.1 `GET /api/v1/health`

服务自身的状态。Agent 判断“服务活着没有、能不能调速”全靠它。

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `ok` | bool | 恒为 true |
| `version` | string | 工具箱版本号 |
| `apiVersion` | string | 接口版本，当前 `1` |
| `port` / `url` | int / string | 实际监听端口与地址 |
| `startedAt` / `uptimeSeconds` | string / int | 服务启动时刻与已运行秒数 |
| `elevated` | bool | 当前进程是否管理员 |
| `pawnIoDriver` | bool | PawnIO 内核驱动是否已装（调速前提） |
| `actionsEnabled` | bool | 是否以 `--allow-actions` 启动 |

### 5.2 `GET /api/v1/system`

```json
{
  "ok": true,
  "collectedAt": "2026-11-14 16:58:35",
  "cpuPercent": 15.7,
  "memory": {
    "usedBytes": 24216461312,
    "totalBytes": 33996648448,
    "percent": 71.2,
    "usedText": "22.6 GB",
    "totalText": "31.7 GB"
  },
  "processCount": 261,
  "machine": {
    "computerName": "HORIZEN",
    "user": "Administrator",
    "os": "Microsoft Windows NT 10.0.26300.0",
    "version": "10.0.26300.0",
    "build": "26300",
    "is64Bit": true,
    "processorCount": 12,
    "systemDrive": "C:\\"
  },
  "uptimeSeconds": 20972
}
```

> `cpuPercent` 是**两次调用之间的平均占用**（和任务管理器同源），
> 第一次调用没有参照点，数值会偏低。想拿稳定值就连调两次、取第二次。

### 5.3 `GET /api/v1/hardware`

WMI 采集，约 2 秒；结果会写盘缓存（`%APPDATA%\Win11Toolbox\hardware.json`，有效期 7 天）。

| 查询参数 | 说明 |
| --- | --- |
| `refresh=true` | 忽略缓存，强制重新采集 |

```json
{
  "ok": true,
  "collectedAt": "2026-11-14 16:52:44",
  "fromDiskCache": true,
  "report": {
    "overview":  [{ "key": "设备名称", "value": "HORIZEN" }],
    "cpu":       [{ "key": "型号", "value": "AMD Ryzen 5 7500F 6-Core Processor" }],
    "memory":    [{ "key": "总容量", "value": "32 GB" }],
    "modules":   [{ "slot": "DIMM_A1", "size": "16 GB", "speed": "6000 MHz" }],
    "gpus":      [{ "title": "AMD Radeon RX 6700 XT", "rows": [] }],
    "board":     [{ "key": "主板型号", "value": "..." }],
    "disks":     [{ "model": "...", "size": "1 TB", "health": "良好" }],
    "volumes":   [{ "mount": "C:\\", "total": "500 GB", "percent": 62.5 }],
    "networks":  [{ "name": "以太网", "ip": "192.168.1.10" }],
    "other":     [{ "key": "...", "value": "..." }]
  }
}
```

`report` 内部的字段名与界面一致，都是给人看的字符串（`"32 GB"`），
要算数值请用 `/api/v1/system` 或 `/api/v1/sensors`。

### 5.4 `GET /api/v1/sensors`

```json
{
  "ok": true,
  "collectedAt": "2026-11-14 16:58:36",
  "superIoDetected": true,
  "cpuTempAvailable": true,
  "driverPresent": true,
  "elevated": true,
  "fanCount": 8,
  "tempCount": 10,
  "fans": [
    {
      "index": 1,
      "name": "Fan #2",
      "hardware": "Nuvoton NCT6799D",
      "rpm": 873,
      "percent": 44.3,
      "controllable": true,
      "minPercent": 0,
      "maxPercent": 100
    }
  ],
  "temps": [
    { "hardware": "Nuvoton NCT6799D", "name": "System", "celsius": 38.0 },
    { "hardware": "AMD Ryzen 5 7500F", "name": "Core (Tctl/Tdie)", "celsius": 45.6 }
  ]
}
```

| 字段 | 说明 |
| --- | --- |
| `index` | 通道序号，调速时用它定位 |
| `rpm` | 转速，**0 表示该接口没接风扇**（不是坏了） |
| `percent` | 当前 PWM 百分比 |
| `controllable` | 是否支持软件调速（笔记本 EC 通道通常是 false） |
| `superIoDetected` | 是否枚举到主板 SuperIO，false 多半是驱动没装 |
| `cpuTempAvailable` | CPU 温度是否读得到 |

### 5.5 `GET /api/v1/voltages`

```json
{
  "ok": true,
  "railCount": 3,
  "otherCount": 19,
  "rails": [
    {
      "label": "+12V",
      "rail": "V12",
      "hardware": "Nuvoton NCT6799D",
      "name": "Voltage #5",
      "identifier": "/lpc/nct6799d/0/voltage/4",
      "nominal": 12,
      "value": 12.08,
      "raw": 1.008,
      "gain": 11.98,
      "min": 12.08,
      "max": 12.08,
      "hasBaseline": true,
      "baseline": 12.08,
      "dropPercent": 0,
      "inTolerance": true,
      "toleranceLow": 11.4,
      "toleranceHigh": 12.6
    }
  ],
  "others": []
}
```

| 字段 | 说明 |
| --- | --- |
| `value` | 换算后的电压（V），两位小数 |
| `raw` | ADC 原始读数 |
| `gain` | 分压系数（本机 NCT6799D：+12V ×11.98、+5V ×5.02、+3.3V ×1） |
| `min` / `max` | 本次服务启动以来的极值 |
| `baseline` / `dropPercent` | 参考基线与相对压降（%），`hasBaseline=false` 时无意义 |
| `inTolerance` | 是否在额定 ±5% 内 |

**精度边界**：NCT6799D 的 ADC 是 8 mV/LSB，+12V 轨乘回后**步进 0.0958 V**。
所以 `value` 的绝对值只可信到 ±0.1 V，**看压降 `dropPercent` 才有意义**。
这与 HWiNFO 对同一通道的读数一致（同源 ADC）。

### 5.6 `GET /api/v1/snapshot`

一次请求拿全三类数据，适合 Agent 轮询（一次采样，省两次开销）：

```json
{
  "ok": true,
  "collectedAt": "2026-11-14 17:02:11",
  "system":   { "cpuPercent": 12.4, "memoryPercent": 71.2, "processCount": 261, "uptimeSeconds": 21188 },
  "sensors":  { "fanCount": 8, "tempCount": 10, "fans": [], "temps": [] },
  "voltages": { "rails": [], "others": [] }
}
```

`sensors` / `voltages` 的结构与各自端点完全相同。

### 5.7 `GET /api/v1/tools`

```json
{
  "ok": true,
  "count": 47,
  "categories": [
    {
      "title": "系统管理",
      "items": [
        { "name": "任务管理器", "description": "查看进程、性能与启动项", "target": "taskmgr.exe" }
      ]
    }
  ]
}
```

### 5.8 `GET /api/v1/settings`

```json
{
  "ok": true,
  "count": 65,
  "categories": [
    {
      "category": "资源管理器",
      "items": [
        {
          "name": "显示文件扩展名",
          "description": "文件名后面显示 .txt / .jpg 这类扩展名，改格式时更方便。",
          "enabled": true,
          "needAdmin": false,
          "needExplorerRestart": false,
          "dangerous": false,
          "registryPath": "HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\Advanced → HideFileExt = 0（默认 1）"
        }
      ]
    }
  ]
}
```

- `enabled`：实时探测的结果，探测失败为 `null`
- `dangerous=true`：界面上会弹确认的危险项
- `guideUrl` 非空：系统不允许程序直接改，只能引导用户手动操作

### 5.9 `GET /api/v1/tray`

```json
{
  "ok": true,
  "count": 47,
  "items": [
    {
      "app": "AIDA64 Business",
      "settingsName": "AIDA64 Business",
      "trayKey": "14570372869379566413",
      "where": "未运行",
      "live": false,
      "exePath": "C:\\图吧工具箱\\...\\aida64.exe",
      "state": 0,
      "tooltip": null
    }
  ]
}
```

`live=true` 表示当前真的在托盘里；`state`：`1`=始终显示，`0`=在溢出区，`-1`=未知。

### 5.10 `GET /api/v1/apps`

读 `Get-AppxPackage` + winget，实测约 **2 秒**，结果缓存 5 分钟。
`refresh=true` 强制重读。

```json
{
  "ok": true,
  "count": 59,
  "items": [
    {
      "name": "31133tmnetworks.PCWattVaultPCEnergyCostTracker",
      "version": "1.0.26.0",
      "source": "当前用户",
      "packageFullName": "..._x64__x0rcqbt6ef600"
    }
  ]
}
```

---

## 6. 写操作端点

> 三个端点都必须以 `--allow-actions` 启动，否则返回 **403**：
> ```json
> { "ok": false, "error": "服务是只读模式。要执行操作，请用 --allow-actions 启动，例如：Win11工具箱.exe --api --allow-actions" }
> ```

### 6.1 `POST /api/v1/tools/launch`

按 `name`（中文名）或 `target`（可执行目标）启动一个工具。

```bash
curl -X POST http://127.0.0.1:5180/api/v1/tools/launch \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d '{"name":"任务管理器"}'
```

```json
{ "ok": true, "launched": "任务管理器", "target": "taskmgr.exe", "message": "已启动" }
```

- 找不到工具 → `ok=false`，提示先查 `/api/v1/tools`
- 目标程序没装（如 `wt.exe`）→ `ok=true` 但 `message` 说明退回了什么

### 6.2 `POST /api/v1/fan/set`

```bash
curl -X POST http://127.0.0.1:5180/api/v1/fan/set \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d '{"index":1,"percent":50}'
```

| 字段 | 说明 |
| --- | --- |
| `index` | 通道序号（来自 `/api/v1/sensors` 的 `index`） |
| `name` | 或者用通道名，如 `"Fan #2"`；`index` 优先 |
| `percent` | 0–100，会被压到该通道的 `minPercent`–`maxPercent` |

```json
{ "ok": true, "channel": "Fan #2", "percent": 50 }
```

前置条件（缺一不可，否则返回可读的错误）：

1. 进程是管理员（`elevated=true`）
2. PawnIO 驱动已装（`pawnIoDriver=true`）
3. 通道 `controllable=true`

### 6.3 `POST /api/v1/fan/auto`

把通道交回 BIOS / 主板默认控制。

```bash
curl -X POST http://127.0.0.1:5180/api/v1/fan/auto \
  -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
  -d '{"index":1}'
```

```json
{ "ok": true, "channel": "Fan #2", "auto": true }
```

### 6.4 `POST /api/v1/settings/set`

```bash
curl -X POST http://127.0.0.1:5180/api/v1/settings/set \
  -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
  -d '{"name":"显示文件扩展名","enabled":true}'
```

```json
{ "ok": true, "name": "显示文件扩展名", "requested": true, "actual": true, "needExplorerRestart": false }
```

- `name` 必须与 `GET /api/v1/settings` 返回的 `name` **完全一致**
- `actual` 是写入后重新探测的结果；和 `requested` 不一致说明没生效
- `needExplorerRestart=true` 时，需要重启资源管理器才看得出来
- 危险项（`dangerous=true`）不会额外拦你，但请自行确认

---

## 7. 错误码

| HTTP | 含义 | 典型原因 |
| --- | --- | --- |
| 200 | 成功 | — |
| 401 | 未授权 | Token 缺失 / 错误 |
| 403 | 禁止 | 只读模式下调写端点 |
| 404 | 路径或对象不存在 | 端点拼错、工具名/设置名不存在 |
| 405 | 方法不对 | 用 GET 打了只接受 POST 的端点 |
| 411 | 缺少 Content-Length | POST 没带请求体，见下 |
| 500 | 服务端异常 | 采集失败等，`error` 里有异常类型 |

业务失败（例如“通道不存在”）统一是 **HTTP 200 + `ok:false`**，
这样 Agent 只解析一个字段就能判断成败；HTTP 状态码只表达传输层语义。

**关于 411**：`POST` 必须带请求体（哪怕 `-d '{}'`），否则 Windows 的 HTTP 层
在进入我们的代码前就拒绝了。用 curl 时加 `-d '{}'` 即可。

---

## 8. Agent 调用示例

### curl

```bash
TOKEN=$(cat "$APPDATA/Win11Toolbox/api-token.txt")
B=http://127.0.0.1:5180

curl -s -H "Authorization: Bearer $TOKEN" $B/api/v1/health
curl -s -H "Authorization: Bearer $TOKEN" $B/api/v1/snapshot
curl -s -H "Authorization: Bearer $TOKEN" "$B/api/v1/hardware?refresh=true"
```

### PowerShell

```powershell
$t = Get-Content "$env:APPDATA\Win11Toolbox\api-token.txt" -Raw
$h = @{ Authorization = "Bearer $($t.Trim())" }

Invoke-RestMethod http://127.0.0.1:5180/api/v1/snapshot -Headers $h |
    Select-Object -ExpandProperty system
```

### Python（标准库，无依赖）

```python
import json, os, urllib.request

home = os.path.expandvars(r"%APPDATA%\Win11Toolbox")
token = open(os.path.join(home, "api-token.txt"), encoding="utf-8").read().strip()

def call(path, payload=None):
    req = urllib.request.Request(
        "http://127.0.0.1:5180" + path,
        data=json.dumps(payload).encode() if payload is not None else None,
        headers={"Authorization": "Bearer " + token,
                 "Content-Type": "application/json"},
        method="POST" if payload is not None else "GET")
    with urllib.request.urlopen(req, timeout=30) as r:
        return json.load(r)

print(call("/api/v1/sensors")["fans"])
```

### Node.js

```js
const fs = require("fs"), path = require("path"), os = require("os");
const token = fs.readFileSync(
  path.join(process.env.APPDATA, "Win11Toolbox", "api-token.txt"), "utf8").trim();

const r = await fetch("http://127.0.0.1:5180/api/v1/voltages", {
  headers: { Authorization: `Bearer ${token}` },
});
console.log((await r.json()).rails.map(x => `${x.label}=${x.value}V`));
```

### 包装成 Agent 工具（伪代码）

```python
TOOLS = [
    {"name": "pc_health",   "endpoint": "GET  /api/v1/health"},
    {"name": "pc_snapshot", "endpoint": "GET  /api/v1/snapshot"},
    {"name": "pc_hardware", "endpoint": "GET  /api/v1/hardware"},
    {"name": "pc_set_fan",  "endpoint": "POST /api/v1/fan/set"},
]
# Agent 侧只需要：拼 URL、带 Bearer、把 JSON 原样交给模型
```

---

## 9. 安全模型

| 边界 | 做法 |
| --- | --- |
| 网络 | 只绑 `127.0.0.1` / `localhost`，不监听 `0.0.0.0`，同网段其他机器连不上 |
| 认证 | 每台机器一份随机 32 位 Token，文件权限随用户账户 |
| 权限分级 | 默认只读；写操作要启动参数显式开启 |
| 危险动作 | 修改设置、调速都走与界面同一条代码路径，不绕过确认逻辑 |
| 日志 | `api.log` 记录每条请求（**不记录 Token**） |
| 不做的事 | 不上传任何数据、不开放远程、不提供提权接口 |

Token 泄露的后果仅限于本机同用户可执行的操作。**换 Token 只需删掉
`api-token.txt` 再重启服务。**

---

## 10. 已知限制

1. **文字版没有 API**：`Win11Toolbox-text.exe` 是精简界面，不含 Services 层。
2. **调速依赖硬件**：需要管理员 + PawnIO 驱动 + 通道可写。笔记本多数走 EC，不可调。
3. **电压精度**：ADC 8 mV/LSB，+12V 步进 0.0958 V，绝对值不可当万用表用。
4. **`/api/v1/apps` 慢**：2 秒左右，请缓存结果，别放进 UI 循环里轮询。
5. **启动成本**：4.8.1 版单文件首次启动要解压内嵌 dll，约 1 秒；之后走系统缓存。
6. **并发**：所有端点可并发调用，硬件采集内部有锁，会排队而不是并行读。
7. **端口冲突**：8080/5180 被占用时换端口；非管理员若绑定失败，按日志提示执行
   `netsh http add urlacl url=http://127.0.0.1:<端口>/ user=%USERNAME%`。

---

## 11. 故障排查

| 现象 | 排查 |
| --- | --- |
| `curl` 连不上 | 进程起了吗？看 `%APPDATA%\Win11Toolbox\api.log` 首行 |
| 退出码 3 | 端口被占用或绑定被拒，日志里有具体原因与 URL ACL 提示 |
| 一直 401 | `api-token.txt` 里有没有换行/空格；用 `ReadAllText().Trim()` 读 |
| 403 | 忘了 `--allow-actions` |
| 411 | POST 没带请求体，加 `-d '{}'` |
| `sensors` 全空 | 驱动没装或没提权；`health` 里看 `pawnIoDriver` / `elevated` |
| `voltages.rails` 少于 3 | 主板芯片未被识别，只返回能确认的轨道，不硬造数据 |
| 中文乱码 | 请求体按 UTF-8 编码；服务端已默认按 UTF-8 解析（仅当显式声明 charset 才照声明） |

---

## 12. 版本策略

- 路径里带 `v1`：`/api/v1/*` 的字段只增不改，破坏性变更走 `/api/v2`
- 新增字段不需要换版本，Agent 应忽略不认识的字段
- `GET /openapi.json` 永远反映当前进程的真实能力，可直接喂给支持 OpenAPI 的框架

---

*对应源码：`Services/AgentApi.cs`（启动入口在 `App.xaml.cs` 的 `--api` 分支）。*
