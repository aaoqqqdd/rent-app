出租设备 Windows 管理客户端需求文档

1. 项目定位

本项目是安装在出租 Windows 设备上的设备管理客户端，不是独立的租赁管理后台。

客户端接入现有租赁网站及后端系统。

设备、客户、租赁订单、租期、付款、合同以及管理员操作均以现有租赁网站为主要管理入口。

Windows 客户端主要负责：

* 识别当前设备；
* 上报设备运行状态；
* 上报基础硬件信息；
* 接收当前租赁状态；
* 显示租期和到期提醒；
* 执行经过授权的远程管理指令；
* 记录客户端运行和指令执行日志；
* 在断网情况下维持必要的本地租期状态；
* 与服务器保持安全、可审计的设备管理通信。

客户端不得直接访问 Cloudflare D1 数据库。

所有数据库访问必须经过统一后端 API。

⸻

2. 产品目标

本项目的核心目标包括：

1. 知道每台出租设备当前是否在线；
2. 知道设备是否正常运行；
3. 将物理设备与网站中的设备记录准确绑定；
4. 将设备与当前租赁订单和租期关联；
5. 在租期即将结束时提醒用户；
6. 在租期结束后显示明确的归还提示；
7. 支持管理员远程延长租期；
8. 支持管理员暂停和恢复设备租赁状态；
9. 支持管理员向指定设备发送服务通知；
10. 保留完整的设备状态变化和管理操作记录；
11. 避免因短暂断网、服务器异常或系统时间修改造成错误控制；
12. 为未来自动验机、维护管理和设备归还流程提供基础能力。

⸻

3. 非目标

第一阶段不将本客户端设计为员工监控、用户行为监控或隐蔽监控软件。

默认不实现以下功能：

* 键盘记录；
* 浏览器历史记录采集；
* 用户聊天记录读取；
* 用户文件内容扫描；
* 摄像头远程开启；
* 麦克风远程开启；
* 持续录屏；
* 隐蔽截图；
* 密码采集；
* 剪贴板内容监控；
* 未明确告知用户的精确位置追踪。

客户端只采集设备管理、运行状态和租赁控制所合理需要的数据。

⸻

4. 系统总体架构

系统与现有租赁网站共用 Cloudflare D1 数据库。

不为 Windows 客户端额外建立独立的客户、设备或租赁数据库。

推荐架构：

                         ┌─────────────────────┐
                         │   Cloudflare D1     │
                         │   Unified Database  │
                         └──────────┬──────────┘
                                    │
                         ┌──────────▼──────────┐
                         │   Unified Backend   │
                         │        API          │
                         └──────┬────────┬─────┘
                                │        │
                 ┌──────────────┘        └──────────────┐
                 │                                      │
       ┌─────────▼─────────┐                 ┌──────────▼──────────┐
       │ Existing Rental   │                 │ Windows Device      │
       │ Website / Admin   │                 │ Management Client   │
       └───────────────────┘                 └─────────────────────┘

原则：

Website → API → D1
Windows Client → API → D1

禁止：

Windows Client → D1

⸻

5. 系统组成

5.1 Windows 客户端

安装在每一台出租 Windows 设备上。

建议由两个主要组件组成：

DeviceAgent.Service
+
DeviceAgent.UI

DeviceAgent.Service

Windows Service，负责：

* 开机启动；
* 后台常驻；
* 设备注册；
* 身份认证；
* 心跳；
* 硬件信息采集；
* 租期同步；
* 指令获取；
* 指令执行；
* 本地状态缓存；
* 日志；
* 自动更新。

DeviceAgent.UI

普通用户可见的小型界面，负责：

* 显示设备编号；
* 显示租赁状态；
* 显示租期；
* 显示到期提醒；
* 显示管理员消息；
* 提供联系客服入口。

UI 不承担核心后台逻辑。

即使 UI 被关闭，Windows Service 仍应正常工作。

⸻

5.2 现有租赁网站管理端

网站继续作为唯一管理员管理入口。

不开发独立 Windows 管理后台。

管理员应通过现有网站完成：

* 设备管理；
* 客户管理；
* 租赁管理；
* 设备绑定；
* 租期修改；
* 远程指令；
* 日志查询；
* 设备维护；
* 客户端版本查看。

⸻

5.3 统一后端 API

统一后端 API 是网站、客户端和数据库之间的唯一可信业务层。

主要职责：

* 用户身份认证；
* 管理员权限检查；
* 设备身份认证；
* 设备注册；
* 心跳接收；
* 设备状态更新；
* 租赁状态查询；
* 设备与租赁绑定；
* 远程指令创建；
* 远程指令领取；
* 指令结果回传；
* 日志记录；
* 幂等处理；
* 数据校验；
* Cloudflare D1 数据读写。

⸻

6. 数据库设计原则

6.1 单一数据源

Cloudflare D1 是设备、客户和租赁业务的单一事实来源。

以下数据不得在网站数据库和客户端数据库中分别维护两套业务真值：

* 客户；
* 设备；
* 租赁订单；
* 租期；
* 当前设备绑定关系；
* 设备业务状态。

Windows 客户端只能保存必要的本地缓存。

本地缓存不是最终业务真值。

⸻

6.2 建议数据表

优先复用现有数据库。

推荐逻辑表包括：

users
customers
devices
rentals
device_assignments
device_credentials
device_heartbeats
device_commands
device_command_results
device_status_history
activity_logs
agent_releases

如果现有网站已经存在：

customers
devices
rentals

应直接复用，不重复创建。

⸻

7. 建议核心字段

7.1 devices

建议包括：

id
device_id
asset_tag
device_name
hostname
manufacturer
brand
model
serial_number
cpu
gpu
ram_bytes
storage_bytes
windows_version
windows_build
battery_health
battery_cycle_count
agent_id
agent_version
business_status
online_status
last_seen_at
last_boot_at
created_at
updated_at

⸻

7.2 device_assignments

用于描述当前租赁绑定关系：

id
device_id
customer_id
rental_id
assignment_status
assigned_at
unassigned_at
created_at
updated_at

⸻

7.3 device_heartbeats

建议存储：

id
device_id
agent_version
online_status
battery_percent
battery_health
disk_free_bytes
network_status
client_timestamp
server_timestamp

不建议永久保存每一次高频 heartbeat。

可以：

* 保留最近状态；
* 对历史 heartbeat 进行聚合；
* 定期清理无业务价值的高频数据。

⸻

7.4 device_commands

id
command_id
device_id
command_type
payload
status
created_by
created_at
expires_at
claimed_at
completed_at

状态：

PENDING
DELIVERED
RUNNING
SUCCEEDED
FAILED
EXPIRED
CANCELLED

⸻

7.5 device_command_results

id
command_id
device_id
success
result_code
result_message
executed_at
reported_at

⸻

7.6 activity_logs

id
actor_type
actor_id
device_id
rental_id
action
result
metadata
created_at

actor_type 示例：

ADMIN
DEVICE
SYSTEM
CUSTOMER

⸻

8. 设备业务状态

设备业务状态和客户端在线状态必须分开。

8.1 Business Status

AVAILABLE
RESERVED
PREPARING
RENTED
OVERDUE
RETURN_PENDING
MAINTENANCE
RETIRED

⸻

8.2 Online Status

ONLINE
OFFLINE
UNKNOWN

例如：

Business Status: RENTED
Online Status: OFFLINE

是完全合法的状态组合。

⸻

9. 客户端运行状态

建议客户端内部维护：

STARTING
REGISTERING
ONLINE
DEGRADED
OFFLINE
UPDATING
ERROR

⸻

10. 设备安装

10.1 安装程序

安装包应支持：

* Windows 10；
* Windows 11；
* x64；
* 管理员权限安装；
* Windows Service 安装；
* UI 组件安装；
* 自动创建所需目录；
* 安装完成后启动 Agent。

⸻

10.2 安装目录

例如：

C:\Program Files\CompanyName\DeviceAgent\

本地数据：

C:\ProgramData\CompanyName\DeviceAgent\

建议分离：

Program Files
→ 程序
ProgramData
→ 配置、缓存、日志

⸻

11. 首次注册

客户端第一次启动时：

Agent Start
↓
读取本地 Device Credential
↓
没有 Credential
↓
执行设备注册
↓
服务器验证注册授权
↓
生成 device_id / agent_id
↓
获得设备 Credential
↓
安全保存 Credential
↓
完成绑定

禁止匿名设备自行无限注册。

可采用：

One-time enrollment token

或：

管理员后台生成设备绑定码

⸻

12. 设备唯一身份

不要只依赖：

Computer Name

因为用户可以修改。

设备身份至少应包括：

device_id
agent_id
device_credential

设备硬件信息可用于辅助识别，但不应该单独作为认证凭证。

⸻

13. 设备凭证

每台设备拥有独立 Credential。

要求：

* 每台设备不同；
* 不允许共享；
* 可以撤销；
* 可以重新生成；
* 不应明文硬编码在程序；
* 不应允许客户端查询其他设备；
* 凭证泄露后可以单独禁用一台设备。

⸻

14. Windows 客户端功能

14.1 开机启动

Windows Service 应配置：

Startup Type:
Automatic (Delayed Start)

避免显著拖慢 Windows 开机。

⸻

14.2 后台运行

Service 负责核心业务。

即使：

* 用户退出 UI；
* UI 崩溃；
* 用户注销 Windows；

Service 仍应继续运行。

⸻

15. 设备信息采集

MVP 上报：

Computer Name
Manufacturer
Brand
Model
Windows Version
Windows Build
CPU
GPU
RAM
Storage Capacity
Storage Free Space
Battery Percentage
Battery Health
Network Status
Agent Version
Last Boot Time
Last Seen

⸻

16. 后续硬件信息

后续版本可增加：

Storage Model
Storage Serial Number
Battery Cycle Count
GPU Driver Version
BIOS Version
BIOS Serial
Wi-Fi Adapter
Bluetooth Adapter

⸻

17. 不采集的数据

默认不采集：

User Documents
Browser History
Browser Cookies
Chat Records
Passwords
Keyboard Input
Clipboard
Personal File Names
Personal File Contents
Camera Images
Microphone Audio

除非未来业务确实需要，并经过单独的隐私、法律和产品评估。

⸻

18. Heartbeat 心跳

客户端必须定时向 API 发送 heartbeat。

建议逻辑：

Online:
每 60–120 秒发送一次 heartbeat

第一版不建议设置过于频繁。

heartbeat 建议包含：

{
  "device_id": "...",
  "agent_version": "...",
  "client_time": "...",
  "uptime_seconds": 10000,
  "battery_percent": 82,
  "network_status": "ONLINE"
}

⸻

19. 在线状态判断

不能由客户端直接写：

online = false

服务器根据：

last_seen_at

计算状态。

例如：

last_seen < 3 minutes
→ ONLINE
3–10 minutes
→ UNKNOWN
> 10 minutes
→ OFFLINE

具体阈值应支持后台配置。

⸻

20. 断网恢复

客户端失去网络后：

API Request Failure
↓
进入 Offline Mode
↓
保留最后有效租赁状态
↓
指数退避重新连接
↓
网络恢复
↓
立即同步服务器状态

不得因为一次 API 请求失败就锁定设备。

⸻

21. 重试策略

建议：

5 sec
10 sec
30 sec
1 min
2 min
5 min

设置最大间隔。

成功连接后恢复正常 heartbeat。

⸻

22. 本地状态缓存

断网期间必须至少保存：

device_id
last_valid_server_time
rental_status
rental_start
rental_end
grace_period
last_command_id
agent_version

本地数据应避免普通用户轻易修改。

⸻

23. 服务器时间

租期判断不得只依赖：

DateTime.Now

因为用户可以修改 Windows 系统时间。

应结合：

Server Time
+
Last Trusted Server Time
+
Monotonic Time / System Uptime

进行判断。

例如：

server_time_at_sync
+
elapsed_monotonic_time
≈ trusted_current_time

⸻

24. 租赁状态同步

客户端定时请求：

Current Rental

需要获得：

rental_id
rental_status
start_at
end_at
grace_period
customer_display_name（仅必要时）
support_information

不应向客户端发送完整客户数据库资料。

⸻

25. 租期提醒

提醒时间必须可配置。

建议默认：

72 hours before
24 hours before
3 hours before
At expiry

客户端避免频繁弹窗骚扰用户。

⸻

26. 提醒类型

即将到期

Your rental ends on:
15 August 2026 18:00
Please save your work and prepare the device for return.

⸻

当日到期

Your rental is due for return today.

⸻

已逾期

This rental period has ended.
Please contact ${company_name} or return the device according to your rental agreement.

⸻

27. 宽限期

必须支持：

grace_period_minutes

例如：

60
180
1440

租赁到期：

END
↓
Grace Period
↓
OVERDUE

不得因为服务器延迟几分钟就立即进入限制模式。

⸻

28. 暂停状态

管理员可以从网站发送：

PAUSE_RENTAL

第一版暂停行为建议：

* 显示明确全屏提示；
* 阻止客户端租赁功能继续进行；
* 明确显示联系客服方式；
* 支持管理员远程恢复。

不建议第一版：

* 删除用户数据；
* 修改 BitLocker；
* 破坏 Windows；
* 删除用户账户；
* 执行不可逆锁定。

⸻

29. 恢复状态

后台执行：

RESUME_RENTAL

客户端：

收到命令
↓
验证命令
↓
取消暂停界面
↓
恢复正常租赁状态
↓
回传执行成功

⸻

30. 延长租期

后台延长：

end_at

数据库首先更新 Rental。

随后客户端同步最新 Rental。

客户端不应自己修改最终租赁日期。

数据真值始终来自服务器。

⸻

31. 远程消息

支持：

SHOW_MESSAGE

payload 示例：

{
  "title": "Rental Reminder",
  "message": "Your rental ends tomorrow."
}

要求：

* 长度限制；
* 防止任意 HTML/script；
* 只显示纯文本或安全格式；
* 记录显示结果。

⸻

32. 远程指令系统

MVP 支持：

SYNC
SHOW_MESSAGE
PAUSE_RENTAL
RESUME_RENTAL
REFRESH_DEVICE_INFO
CHECK_UPDATE

⸻

33. 命令执行原则

每个命令必须有：

command_id
device_id
command_type
created_at
expires_at

客户端收到后验证：

1. 是否属于自己；
2. 是否过期；
3. 是否已经执行；
4. command_type 是否支持；
5. payload 是否合法。

⸻

34. 命令幂等

同一个：

command_id

只能执行一次。

例如服务器重发：

PAUSE #123
PAUSE #123
PAUSE #123

客户端不能当成三个不同操作。

⸻

35. 指令超时

每个命令应有：

expires_at

例如：

SHOW_MESSAGE
有效期 24 小时
PAUSE
有效期 1 小时

超过时间客户端不执行，并返回：

EXPIRED

⸻

36. 自动更新

客户端必须支持自动更新。

服务器维护：

latest_version
minimum_supported_version
download_url
file_hash
release_channel

客户端：

Check Update
↓
Download
↓
Verify Signature
↓
Verify Hash
↓
Install
↓
Restart Service
↓
Report New Version

⸻

37. 更新安全

更新包必须：

* HTTPS 下载；
* 验证数字签名；
* 验证文件 Hash；
* 拒绝未签名或签名异常版本；
* 防止降级到明显不安全旧版本。

⸻

38. Agent Version 管理

后台显示：

Installed:
1.2.3
Latest:
1.3.0
Status:
Update Available

可筛选：

Outdated Agents

⸻

39. 客户端日志

记录：

Agent Started
Agent Stopped
Registration Success/Failed
Heartbeat Error
Connected
Disconnected
Rental Synced
Rental Status Changed
Reminder Displayed
Command Received
Command Executed
Command Failed
Update Started
Update Completed
Update Failed
Unhandled Exception

⸻

40. 日志隐私原则

日志不得记录：

Passwords
Payment Card Details
Personal File Contents
Authentication Tokens
完整 API Secret

必要敏感字段必须脱敏。

⸻

41. 日志级别

建议：

DEBUG
INFO
WARNING
ERROR
CRITICAL

生产环境默认：

INFO

⸻

42. 本地日志轮转

避免无限增长。

例如：

单文件最大 10 MB
保留 10 个日志文件

或根据时间：

保留 14–30 天

⸻

43. 管理后台设备列表

后台至少显示：

字段	说明
Device ID	设备唯一编号
Asset Tag	资产编号
Device Name	设备名称
Business Status	业务状态
Online Status	在线状态
Current Customer	当前客户
Rental End	当前租期结束
Last Seen	最后在线
Agent Version	客户端版本
Battery Health	电池健康
Alerts	当前告警

⸻

44. 设备详情页

建议 Tabs：

Overview
Hardware
Current Rental
Rental History
Health
Commands
Activity Logs
Maintenance
Agent

⸻

45. Overview

显示：

Device ID
Asset Tag
Online / Offline
Last Seen
Business Status
Current Rental
Agent Version
Windows Version
Basic Hardware Health

⸻

46. Hardware

显示：

Brand
Model
Serial Number
CPU
GPU
RAM
Storage
Storage Free
Battery
Windows

⸻

47. Current Rental

显示：

Rental ID
Customer
Start
End
Status
Grace Period

管理员操作：

Extend
End Rental
Pause
Resume

⸻

48. Commands

显示：

Command
Created
Created By
Status
Executed
Result

⸻

49. Activity Logs

显示：

Timestamp
Actor
Action
Target
Result

例如：

2026-08-15 10:00
Admin A
PAUSE_RENTAL
GS-LAP-001
SUCCESS

⸻

50. 管理后台权限

至少建议：

OWNER
ADMIN
STAFF
VIEWER

⸻

OWNER

可执行：

* 全部操作；
* 管理管理员；
* 管理安全设置；
* 删除/报废设备。

⸻

ADMIN

可：

* 管理设备；
* 管理租赁；
* 下发远程命令；
* 查看日志。

⸻

STAFF

可：

* 查看设备；
* 处理普通租赁；
* 执行有限设备操作。

⸻

VIEWER

只读。

⸻

51. 高风险操作

以下操作需要二次确认：

PAUSE DEVICE
END RENTAL
UNBIND DEVICE
REVOKE DEVICE CREDENTIAL
RETIRED DEVICE

确认界面至少显示：

Device
Customer
Current Rental
Action

⸻

52. 设备解绑

设备解绑必须：

1. 管理员确认；
2. 检查是否存在 Active Rental；
3. 如果存在 Active Rental，默认禁止直接解绑；
4. 撤销旧 Credential；
5. 写 Activity Log。

⸻

53. 设备报废

状态：

RETIRED

报废后：

* 不再接受租赁；
* 设备 credential 可以撤销；
* 历史数据保留；
* 不删除历史 Rental；
* 不删除历史 Activity Log。

⸻

54. 安全要求

通信

必须：

HTTPS / TLS

禁止：

Plain HTTP

⸻

55. API 身份认证

管理员 API 和 Device API 必须分离权限模型。

例如：

/api/admin/*
/api/device/*

Device Token 不能访问 Admin API。

⸻

56. 最小权限

设备只能：

GET own configuration
GET own rental status
GET own commands
POST own heartbeat
POST own device state
POST own command results
POST own logs

不能：

GET all customers
GET other rentals
GET other devices
UPDATE rental pricing
CREATE administrator

⸻

57. API Rate Limit

设备接口应设置合理 Rate Limit。

防止：

* 客户端 Bug；
* 恶意请求；
* 无限循环；
* API 滥用。

⸻

58. 防止重放

重要命令或请求可以包含：

request_id
timestamp
nonce

后端拒绝明显重复或过期请求。

⸻

59. 防止修改系统时间绕过租期

租期控制必须以服务器时间为主。

客户端本地时间只作为辅助。

检测明显时间跳变，例如：

15 Aug 12:00
↓
10 Aug 12:00

应：

记录异常
保持上一次可信服务器时间模型
尝试重新同步服务器

而不是直接相信系统时间。

⸻

60. 防止普通用户随意卸载

客户端可以要求：

* Windows 管理员权限才能卸载；
* 卸载器受正常 Windows 权限保护；
* 卸载行为写入日志；
* 后台发现设备长时间失联。

不建议采用类似恶意软件的隐藏或破坏系统机制。

⸻

61. Agent 停止检测

服务器通过 heartbeat 发现：

Device previously rented
+
Agent unexpectedly offline

后台产生：

Agent Offline Alert

但短时间离线不应立即视为恶意行为。

⸻

62. 告警系统

MVP 可支持：

Device Offline
Rental Ending Soon
Rental Expired
Agent Outdated
Agent Error

⸻

63. 通知去重

同一事件避免反复通知。

例如设备连续离线 5 小时：

不要每 5 分钟发一次 Email。

建议：

Alert Open
↓
Notify Once
↓
Reminder after configured interval
↓
Resolved

⸻

64. 客户端 UI

建议保持简单。

┌────────────────────────────────┐
│ ${company_name}                │
│ Device Management              │
│                                │
│ Device ID                      │
│ GS-LAP-001                     │
│                                │
│ Rental Status                  │
│ Active                         │
│                                │
│ Rental End                     │
│ 20 August 2026, 6:00 PM        │
│                                │
│ [ Rental Information ]         │
│ [ Contact Support ]            │
└────────────────────────────────┘

⸻

65. 暂停 UI

建议：

This rental device is currently paused.
Please contact ${company_name} for assistance.
Device ID:
GS-LAP-001
[ Contact Support ]

必须明确这是出租设备管理状态。

⸻

66. 用户透明度

客户端不应伪装或隐藏其业务目的。

建议程序名称：

${company_name} Device Management

Windows Installed Apps 中可正常看到。

⸻

67. Return Mode（V2）

后续增加：

RETURN_PENDING

客户准备归还时显示：

Please back up your files and sign out of personal accounts before returning this device.

⸻

68. 自动验机（V2）

客户端自动检测：

CPU
GPU
RAM
Storage
Storage Serial
Battery Health
Battery Cycles
Wi-Fi Adapter
Camera Device
Windows Activation

⸻

69. 硬件变更检测（V2）

出租前生成：

Hardware Snapshot

归还后再次生成。

比较：

Before
vs
After

例如：

SSD Serial Changed
RAM Capacity Changed
GPU Missing

⸻

70. 维护模式（V2）

设备状态：

MAINTENANCE

记录：

Reason
Assigned Staff
Start Time
Notes
Completion Time

⸻

71. 软件部署（未来）

如果未来实现软件安装功能，必须：

* 使用预定义软件包；
* 服务器明确授权；
* 验证软件包签名；
* 记录操作；
* 禁止任意无审计执行。

⸻

72. 远程脚本（未来）

不建议作为 MVP。

如果未来实现：

* 仅限高权限管理员；
* 使用批准脚本库；
* 禁止普通后台用户任意上传 PowerShell；
* 完整审计；
* 二次确认；
* 超时限制。

⸻

73. 远程协助（未来）

如果提供远程协助，应设计为：

Customer requests support
↓
Customer sees confirmation
↓
Temporary support session
↓
Session ends

不应默认建立隐蔽持续远程桌面访问。

⸻

74. 截图功能

默认不开发管理员静默截图。

如果未来确有客服远程支持需求，应放入独立的远程协助功能中，并提供用户可见的会话状态。

⸻

75. 位置功能

MVP 不实现持续位置追踪。

如果未来针对遗失设备设计 Lost Mode，应单独评估：

* 技术需求；
* 法律依据；
* 隐私政策；
* 用户告知；
* 数据保存期限。

⸻

76. API 建议

设备注册

POST /api/device/enroll

⸻

Heartbeat

POST /api/device/heartbeat

⸻

获取当前状态

GET /api/device/state

⸻

获取当前租赁

GET /api/device/rental

⸻

获取命令

GET /api/device/commands

⸻

上报命令结果

POST /api/device/commands/{command_id}/result

⸻

上报设备信息

POST /api/device/inventory

⸻

上报日志

POST /api/device/events

⸻

77. API 响应格式

建议统一：

{
  "success": true,
  "data": {},
  "error": null,
  "request_id": "..."
}

错误：

{
  "success": false,
  "data": null,
  "error": {
    "code": "DEVICE_UNAUTHORIZED",
    "message": "Device credential is invalid."
  },
  "request_id": "..."
}

⸻

78. 主要错误代码

建议：

DEVICE_UNAUTHORIZED
DEVICE_REVOKED
DEVICE_NOT_FOUND
RENTAL_NOT_FOUND
RENTAL_ENDED
COMMAND_NOT_FOUND
COMMAND_EXPIRED
COMMAND_ALREADY_COMPLETED
INVALID_REQUEST
RATE_LIMITED
SERVER_ERROR

⸻

79. 网络异常

客户端遇到：

HTTP 500
Timeout
DNS Failure
No Internet

不得崩溃。

应：

记录错误
↓
进入 Retry
↓
继续使用最后可信配置

⸻

80. 服务端异常保护

如果 D1 或后端 API 暂时不可用：

* 网站可以显示服务异常；
* Client 使用最后可信状态；
* 不自动执行新的限制；
* 网络恢复后同步。

原则：

Fail safe，不因为服务器暂时故障误伤正常租赁用户。

⸻

81. MVP 范围

第一版必须完成：

1. Windows Installer；
2. Windows Service；
3. 客户端 UI；
4. Device ID；
5. 安全设备注册；
6. Device Credential；
7. 开机启动；
8. Heartbeat；
9. Online / Offline；
10. Last Seen；
11. 基础硬件采集；
12. Agent Version；
13. 当前 Rental 同步；
14. Rental Start / End；
15. 租期提醒；
16. Grace Period；
17. Remote Extend；
18. Remote Pause；
19. Remote Resume；
20. Remote Message；
21. Command Queue；
22. Command Result；
23. Local Cache；
24. Offline Retry；
25. Trusted Server Time；
26. Basic Activity Logs；
27. 后台设备列表；
28. 后台设备详情；
29. 管理员权限检查；
30. 高风险操作二次确认。

⸻

82. MVP 不做

第一版明确不做：

Remote Desktop
Remote Shell
Arbitrary PowerShell
File Browser
File Download
File Upload
Screen Capture
Camera
Microphone
Location Tracking
USB Blocking
Windows Update Management
Factory Reset
Automatic Windows Reimage
Advanced Hardware Inspection

这样可以控制第一版复杂度和安全风险。

⸻

83. MVP 验收标准

安装

* 新设备能够成功安装客户端；
* Service 能够自动启动；
* 重启电脑后 Service 自动运行；
* UI 可以正常打开。

⸻

注册

* 新设备能够通过合法 enrollment 流程注册；
* 后台能够看到新设备；
* 每台设备拥有唯一 Device ID；
* 每台设备拥有独立 Credential。

⸻

在线状态

* 在线设备后台显示 Online；
* 客户端停止 heartbeat 后在合理时间内显示 Offline；
* 恢复网络后自动重新显示 Online；
* Last Seen 准确更新。

⸻

设备信息

后台能够查看：

* Hostname；
* Windows；
* CPU；
* GPU；
* RAM；
* Storage；
* Battery（如适用）；
* Agent Version。

⸻

租赁

后台能够：

* 将设备绑定到现有 Rental；
* 设置开始时间；
* 设置结束时间；
* 查看当前绑定关系。

客户端能够：

* 获取当前 Rental；
* 显示租赁状态；
* 显示租赁结束时间。

⸻

到期提醒

* 可以配置提醒时间；
* 到期前正确显示提醒；
* 到期后显示归还提示；
* 不因几分钟断网立即错误进入限制状态。

⸻

延期

管理员修改租期后：

Website
↓
API
↓
Database
↓
Client Sync
↓
New Rental End

客户端无需重新安装或人工修改。

⸻

Pause

管理员执行 Pause：

* 后端创建 Command；
* 客户端收到；
* 客户端执行；
* 用户看到明确 Pause 状态；
* 后端收到执行结果；
* Activity Log 可查看。

⸻

Resume

管理员执行 Resume 后：

* 客户端恢复；
* 结果回传；
* 管理后台显示成功。

⸻

Offline

设备断网后：

* Agent 不崩溃；
* 保留最后可信租赁状态；
* 自动重试；
* 网络恢复后自动同步。

⸻

系统时间

修改 Windows 系统时间不能简单绕过租期判断。

⸻

安全

* Device A 不能读取 Device B 的数据；
* Device Token 不能调用管理员接口；
* 撤销 Device Credential 后设备无法继续访问 API；
* 所有远程操作有管理员、时间、设备和结果记录。

⸻

84. 推荐开发技术栈

Windows 客户端：

C#
.NET
Windows Service
WPF

后端：

Existing Cloudflare Backend
+
Cloudflare Workers API
+
Cloudflare D1

如果现有网站已经使用 Cloudflare Workers，则优先继续复用现有技术栈。

本地缓存：

SQLite

或者对于非常简单的 MVP：

Encrypted / protected local configuration
+
JSON state cache

⸻

85. 推荐项目结构

DeviceAgent.sln
├── DeviceAgent.Service
│   ├── Heartbeat
│   ├── Enrollment
│   ├── Authentication
│   ├── DeviceInventory
│   ├── RentalSync
│   ├── Commands
│   ├── Updates
│   ├── Logging
│   └── LocalState
│
├── DeviceAgent.UI
│   ├── MainWindow
│   ├── RentalStatus
│   ├── Notifications
│   └── Support
│
├── DeviceAgent.Core
│   ├── Models
│   ├── Interfaces
│   ├── Configuration
│   └── SharedLogic
│
└── DeviceAgent.Tests

⸻

86. 推荐开发顺序

Phase 1 — Agent 基础

Install
Service
Device ID
Enrollment
Authentication
Heartbeat

⸻

Phase 2 — Device Inventory

Hardware
Windows
Battery
Agent Version

⸻

Phase 3 — Rental Integration

Rental Sync
Start / End
UI
Reminder

⸻

Phase 4 — Remote Commands

Command Queue
Message
Pause
Resume
Extend
Result

⸻

Phase 5 — Reliability

Offline Cache
Retry
Server Time
Idempotency
Logging

⸻

Phase 6 — Production Security

Credential Rotation
Revocation
Rate Limit
Code Signing
Update Signing
Permission Audit

⸻

87. 最终核心原则

本项目应遵循以下原则：

单一数据源

租赁业务真值保存在现有网站和统一后端。

客户端最小权限

客户端只能够访问自己的设备和租赁状态。

Fail Safe

服务器或网络暂时故障不能直接导致正常用户设备被错误限制。

可恢复

远程管理操作应优先采用可恢复方式。

可审计

重要操作必须记录：

Who
What
Which Device
When
Result

用户透明

客户应知道设备安装了用于租赁管理的客户端。

最少数据

只采集设备管理和租赁服务实际需要的信息。

安全优先

远程管理能力必须经过身份认证、权限检查、日志记录和必要的二次确认。

⸻

88. 项目成功标准

当本系统投入使用后，管理员应能够仅通过现有租赁网站回答以下问题：

这台设备是谁的？

当前租给谁？

租期什么时候结束？

设备现在在线吗？

最后什么时候在线？

当前 Agent 是什么版本？

设备基本硬件是什么？

到期提醒有没有执行？

是否被暂停？

谁执行了暂停？

指令有没有成功？

客户延长租期后设备有没有同步？

设备断网后有没有自动恢复？

如果这些问题均能够可靠回答，MVP 即基本达到产品目标。