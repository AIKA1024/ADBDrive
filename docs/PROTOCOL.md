# ADB 协议实测笔记

本文记录实现本工具时**实测确认**的字节布局。ADB 的 sync v2 结构在官方文档里几乎没有说明，
这里的结论全部来自对真机（Android 12 模拟器，platform-tools 37）的抓包验证，可作为实现参考。

## 1. host / smart-socket 层

请求：`4 位十六进制长度 + ASCII 命令`，例如 `host:transport:127.0.0.1:5555`。

应答：`OKAY` 或 `FAIL`；`FAIL` 后跟 `4 位十六进制长度 + 文本`。

常用命令：

| 命令 | 说明 |
| --- | --- |
| `host:version` | server 版本（如 `0029`） |
| `host:devices-l` | 设备列表（OKAY 后跟 `4 位长度 + 文本`） |
| `host:connect:<host>:<port>` | 连接远程设备 |
| `host:disconnect:<host>:<port>` | 断开 |
| `host-serial:<serial>:features` | 设备 feature 列表（逗号分隔） |
| `host:transport:<serial>` | 把连接切到某台设备，之后要再发服务名 |

切到 transport 后发送的服务名本身也要带长度前缀，例如 `sync:`、`shell,v2,raw:ls`。

`features` 里与本项目相关的：

```
shell_v2, cmd, stat_v2, ls_v2, sendrecv_v2, ...
```

## 2. sync 层通用帧

`4 字节 ASCII id + 4 字节小端长度 + 负载`（`DATA` / `DONE` / `FAIL` / 请求头）。

## 3. `LST2`（stat v2）

请求负载：`路径 + '\0'`。

应答**不是** `id + 长度 + 负载`，而是固定 72 字节：

```
offset  size  field
0       4     "LST2"
4       4     error (u32, 0 成功；2=ENOENT 13=EACCES 20=ENOTDIR)
8       8     dev
16      8     ino
24      4     mode        (S_IFMT 高 4 位：0x4000 目录 / 0x8000 普通 / 0xA000 符号链接)
28      4     nlink
32      4     uid
36      4     gid
40      8     size
48      8     atime       ← 注意：atime 在 mtime 之前
56      8     mtime
64      8     ctime
```

> 踩坑：一开始按「mtime 在 atime 之前」解析，结果所有文件时间都变成 1970。
> 用 `touch -m -d @1700000001` 造一个 atime≠mtime 的文件，dump 出 48=当前时间、56=被设置的时间，才定位到顺序。

stat 的 **v1** 版本（老设备）是 16 字节裸结构：`"STAT" + mode(u32) + size(u32) + time(u32)`，
`mode == 0` 表示失败（区分不出 ENOENT / EACCES）。

## 4. `LIS2` / `LIST`（目录）

v2 每个条目：

```
" DNT2" (4) + 上面 68 字节 stat 结构（含 error 字段） + namelen(u32) + name
```

遍历结束是 `"DONE" + u32(0)`；出错是 `"FAIL" + u32(len) + message`。
**没有** `id + 长度` 的通用头——第一版按 `header(8) + struct` 读，多了 4 字节，把文件名前 4 个字符当成了 namelen。

v1 每个条目：

```
"DENT" + mode(u32) + size(u32) + time(u32) + namelen(u32) + name
```

`DONE` 同样后跟 4 字节。

## 5. `RECV`（读文件）

请求：`"RECV" + u32(len) + 路径 + '\0'`。

之后是 `DATA + u32(len) + 数据` 的序列，直到 `DONE + u32(0)`；出错是 `FAIL + u32(len) + message`。
单帧最大 64KB，必须流式消费。

## 6. `SEND`（写文件）

请求负载是 **`路径 + "," + 八进制/十进制模式字符串 + '\0'`**：

```
"SEND" + u32(len) + "/sdcard/a.txt,420" + '\0'
```

> 踩坑：只传路径会收到 `missing , in ID_SEND_V1`。`420` 是十进制 0644。

随后循环 `DATA + u32(len) + 数据`，结束时发 `DONE + u32(mtime秒)`，
再读 `OKAY + u32(0)` 或 `FAIL + u32(len) + message`。

> 踩坑：`DONE` 里的 mtime 传 0 不会被当成「现在」，而是真的把文件时间设成 1970。
> 必须传真实时间戳（u32，秒）。

## 7. `shell,v2,raw:<command>`

服务建立后是包序列：`1 字节 id + 4 字节小端长度 + 负载`。

| id | 含义 |
| --- | --- |
| 1 | stdout |
| 2 | stderr |
| 3 | exit（负载 1 字节 = 退出码） |
| 4 | close stdin |

老 adbd 不支持 v2 时，`shell:<command>` 只能拿到合并后的输出且没有退出码。
