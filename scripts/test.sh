#!/bin/bash
# 应用分身 —— 隔离能力自动化测试
# 用法: bash scripts/test.sh
set -u
ROOT="/d/fenshen"
TD="$ROOT/test"; DATA="$TD/data"; OUT="$TD/out"
export FENSHEN_DATA_DIR='D:\fenshen\test\data'
EXE="$ROOT/dist/AppClone.exe"
SELFTEST="$ROOT/dist/sandbox/selftest.exe"

PASS=0; FAIL=0
ok()   { PASS=$((PASS+1)); echo "  [PASS] $1"; }
bad()  { FAIL=$((FAIL+1)); echo "  [FAIL] $1"; }

wait_file() { # wait_file <path> <timeout_x100ms>
  for _ in $(seq 1 "$2"); do [ -f "$1" ] && return 0; sleep 0.1; done
  return 1
}

echo "== 准备测试数据 =="
rm -rf "$TD"; mkdir -p "$OUT" "$DATA"
# 清理上一次运行可能残留的注册表状态（profiles 已随 $TD 删除，但注册表根不会）
MSYS_NO_PATHCONV=1 reg delete 'HKCU\Software\FenShenSandbox' /f >/dev/null 2>&1
MSYS_NO_PATHCONV=1 reg delete 'HKCU\Software\FenShenSelfTest' /f >/dev/null 2>&1

cat > "$DATA/apps.json" <<'EOF'
{
  "apps": [
    { "Id": "app_env", "Name": "EnvTest", "ExePath": "C:\\Windows\\System32\\cmd.exe",
      "Args": "/c set > \"D:\\fenshen\\test\\out\\env.txt\" 2>&1", "AddedAt": "2026-08-29T00:00:00" },
    { "Id": "app_reg", "Name": "RegTest", "ExePath": "D:\\fenshen\\dist\\sandbox\\selftest.exe",
      "Args": "clone1 \"D:\\fenshen\\test\\out\\reg1.txt\"", "AddedAt": "2026-08-29T00:00:00" },
    { "Id": "app_mutex", "Name": "MutexTest", "ExePath": "D:\\fenshen\\dist\\sandbox\\selftest.exe",
      "Args": "t \"D:\\fenshen\\test\\out\\mutex.txt\"", "AddedAt": "2026-08-29T00:00:00" },
    { "Id": "app_dev", "Name": "DeviceTest", "ExePath": "D:\\fenshen\\dist\\sandbox\\selftest.exe",
      "Args": "d1 \"D:\\fenshen\\test\\out\\device.txt\"", "AddedAt": "2026-08-29T00:00:00" }
  ],
  "clones": {
    "app_env":   [ { "Id": "clone_env", "Name": "分身 1", "DeepIsolation": false, "DeviceClone": false, "CreatedAt": "2026-08-29T00:00:00" } ],
    "app_reg":   [ { "Id": "clone_reg", "Name": "分身 1", "DeepIsolation": true,  "DeviceClone": false, "CreatedAt": "2026-08-29T00:00:00" } ],
    "app_mutex": [ { "Id": "clone_m1", "Name": "分身 1", "DeepIsolation": true,  "DeviceClone": false, "CreatedAt": "2026-08-29T00:00:00" },
                   { "Id": "clone_m2", "Name": "分身 2", "DeepIsolation": true,  "DeviceClone": false, "CreatedAt": "2026-08-29T00:00:00" } ],
    "app_dev":   [ { "Id": "dev1", "Name": "分身 1", "DeepIsolation": true, "DeviceClone": true, "CreatedAt": "2026-08-29T00:00:00" },
                   { "Id": "dev2", "Name": "分身 2", "DeepIsolation": true, "DeviceClone": true, "CreatedAt": "2026-08-29T00:00:00" } ]
  }
}
EOF

# =====================================================================
echo "== 测试 1：环境变量隔离（标准隔离） =="
"$EXE" --launch app_env clone_env
if wait_file "$OUT/env.txt" 100; then
  home=$(grep '^USERPROFILE=' "$OUT/env.txt" | head -1)
  appdata=$(grep '^APPDATA=' "$OUT/env.txt" | head -1)
  temp=$(grep '^TEMP=' "$OUT/env.txt" | head -1)
  echo "    $home | $appdata | $temp"
  case "$home" in
    "USERPROFILE=D:\\fenshen\\test\\data\\profiles\\app_env\\clone_env\\home") ok "USERPROFILE 已重定向" ;;
    *) bad "USERPROFILE 未重定向: $home" ;;
  esac
  case "$appdata" in
    "APPDATA=D:\\fenshen\\test\\data\\profiles\\app_env\\clone_env\\home\\AppData\\Roaming") ok "APPDATA 已重定向" ;;
    *) bad "APPDATA 未重定向: $appdata" ;;
  esac
  case "$temp" in
    "TEMP=D:\\fenshen\\test\\data\\profiles\\app_env\\clone_env\\home\\Temp") ok "TEMP 已重定向" ;;
    *) bad "TEMP 未重定向: $temp" ;;
  esac
else
  bad "env.txt 未生成（启动失败？）"
fi

# =====================================================================
echo "== 测试 2：注册表隔离（深度隔离） =="
MSYS_NO_PATHCONV=1 reg add 'HKCU\Software\FenShenSelfTest' /v marker /t REG_SZ /d BASE /f >/dev/null
"$EXE" --launch app_reg clone_reg
if wait_file "$OUT/reg1.txt" 200; then
  echo "    $(cat "$OUT/reg1.txt" | tr '\n' ' ')"
  grep -q "REG: prev=BASE now=clone1" "$OUT/reg1.txt" \
    && ok "分身读到了快照值(BASE)并写入了新值" \
    || bad "分身内注册表读写异常: $(cat "$OUT/reg1.txt")"
else
  bad "reg1.txt 未生成（启动失败？）"
fi

real=$(MSYS_NO_PATHCONV=1 reg query 'HKCU\Software\FenShenSelfTest' /v marker 2>/dev/null | grep marker)
echo "    真实HKCU: $real"
case "$real" in
  *BASE*) ok "真实 HKCU 未被污染（隔离生效）" ;;
  *) bad "真实 HKCU 被写入: $real" ;;
esac

# 第二次启动：应读到分身自己的持久化数据
sed -i 's/clone1 \\"D:\\\\fenshen\\\\test\\\\out\\\\reg1.txt\\"/clone2 \\"D:\\\\fenshen\\\\test\\\\out\\\\reg2.txt\\"/' "$DATA/apps.json"
"$EXE" --launch app_reg clone_reg
if wait_file "$OUT/reg2.txt" 200; then
  echo "    $(cat "$OUT/reg2.txt" | tr '\n' ' ')"
  grep -q "REG: prev=clone1 now=clone2" "$OUT/reg2.txt" \
    && ok "分身注册表数据跨启动持久化（独立于真实 HKCU）" \
    || bad "第二次启动读取异常: $(cat "$OUT/reg2.txt")"
else
  bad "reg2.txt 未生成"
fi

# =====================================================================
echo "== 测试 3：互斥体改名实现多开（深度隔离） =="
# 对照组：未注入时，重叠运行的第二实例应报 SECOND
rm -f "$OUT/ctl.txt"
"$SELFTEST" ctlA "$OUT/ctl.txt" &
sleep 0.3
"$SELFTEST" ctlB "$OUT/ctl.txt"
sleep 3.5
first_ctl=$(grep -c "MUTEX: FIRST" "$OUT/ctl.txt" || true)
second_ctl=$(grep -c "MUTEX: SECOND" "$OUT/ctl.txt" || true)
if [ "$first_ctl" = "1" ] && [ "$second_ctl" = "1" ]; then
  ok "对照组符合预期（单实例生效：FIRST+SECOND）"
else
  bad "对照组异常: FIRST=$first_ctl SECOND=$second_ctl"
fi

# 实验组：先顺序预热两个分身的注册表快照（与 GUI 创建分身时的行为一致），
# 再同时启动，应都报 FIRST
rm -f "$OUT/mutex.txt"
"$EXE" --launch app_mutex clone_m1; sleep 4
"$EXE" --launch app_mutex clone_m2; sleep 4
rm -f "$OUT/mutex.txt"
"$EXE" --launch app_mutex clone_m1 &
"$EXE" --launch app_mutex clone_m2 &
wait
sleep 6
first_n=$(grep -c "MUTEX: FIRST" "$OUT/mutex.txt" 2>/dev/null || true)
second_n=$(grep -c "MUTEX: SECOND" "$OUT/mutex.txt" 2>/dev/null || true)
echo "    FIRST=$first_n SECOND=$second_n"
if [ "$first_n" = "2" ] && [ "$second_n" = "0" ]; then
  ok "两个分身同时运行且互不冲突（多开解锁生效）"
else
  bad "多开结果异常: FIRST=$first_n SECOND=$second_n"
fi

# =====================================================================
echo "== 测试 4：设备分身（设备指纹随机化） =="
realguid=$(MSYS_NO_PATHCONV=1 reg query 'HKLM\SOFTWARE\Microsoft\Cryptography' /v MachineGuid 2>/dev/null | grep -oE '[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}')

parse_dev() { # parse_dev <file> -> 设 GUID/CN/VOL
  guid=$(grep -o 'guid=[0-9a-fA-F-]*' "$1" | head -1 | cut -d= -f2)
  cn=$(grep -o 'cn=[^ ]*' "$1" | head -1 | cut -d= -f2)
  vol=$(grep -o 'vol=[0-9A-Fa-f]*' "$1" | head -1 | cut -d= -f2)
}

launch_dev() { # launch_dev <cloneId>
  rm -f "$OUT/device.txt"
  "$EXE" --launch app_dev "$1"
  for _ in $(seq 1 300); do grep -q "DEV2:" "$OUT/device.txt" 2>/dev/null && break; sleep 0.2; done
  sleep 0.5
}

launch_dev dev1; parse_dev "$OUT/device.txt"; g1=$guid; c1=$cn; v1=$vol
echo "    分身1: guid=$g1 cn=$c1 vol=$v1"
launch_dev dev2; parse_dev "$OUT/device.txt"; g2=$guid; c2=$cn; v2=$vol
# 延迟加载模块（DEV2 行）应与主程序的假设备一致
g2late=$(grep "DEV2:" "$OUT/device.txt" | grep -o 'guid=[0-9a-fA-F-]*' | head -1 | cut -d= -f2)
echo "    分身2: guid=$g2 cn=$c2 vol=$v2 (late=$g2late)"
launch_dev dev1; parse_dev "$OUT/device.txt"; g3=$guid; c3=$cn

if [ -n "$g1" ] && [ "$g1" != "$g2" ]; then ok "两个分身 MachineGuid 互不相同"; else bad "MachineGuid 未区分: $g1 / $g2"; fi
if [ -n "$c1" ] && [ "$c1" != "$c2" ]; then ok "两个分身设备名互不相同"; else bad "设备名未区分: $c1 / $c2"; fi
if [ -n "$v1" ] && [ "$v1" != "$v2" ]; then ok "两个分身卷序列号互不相同"; else bad "卷序列号未区分: $v1 / $v2"; fi
if [ "$g3" = "$g1" ] && [ "$c3" = "$c1" ]; then ok "同一分身设备身份跨启动保持稳定"; else bad "设备身份不稳定: $g3/$g1 $c3/$c1"; fi
if [ -n "$realguid" ] && [ "$g1" != "$realguid" ]; then ok "分身设备 ID 与真实设备不同"; else bad "分身与真实设备相同: $g1"; fi
realguid2=$(MSYS_NO_PATHCONV=1 reg query 'HKLM\SOFTWARE\Microsoft\Cryptography' /v MachineGuid 2>/dev/null | grep -oE '[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}')
if [ "$realguid2" = "$realguid" ]; then ok "真实 HKLM 设备指纹未被修改"; else bad "真实 HKLM 被污染!"; fi
if [ -n "$g2late" ] && [ "$g2late" = "$g2" ]; then ok "延迟加载模块同样读到假设备 ID"; else bad "延迟加载模块未被 Hook: '$g2late' vs '$g2'"; fi

# =====================================================================
echo "== 清理 =="
MSYS_NO_PATHCONV=1 reg delete 'HKCU\Software\FenShenSelfTest' /f >/dev/null 2>&1
MSYS_NO_PATHCONV=1 reg delete 'HKCU\Software\FenShenSandbox\app_reg' /f >/dev/null 2>&1
MSYS_NO_PATHCONV=1 reg delete 'HKCU\Software\FenShenSandbox\app_mutex' /f >/dev/null 2>&1
MSYS_NO_PATHCONV=1 reg delete 'HKCU\Software\FenShenSandbox\app_dev' /f >/dev/null 2>&1
taskkill //IM selftest.exe //F >/dev/null 2>&1

echo
echo "==== 结果: $PASS 通过, $FAIL 失败 ===="
[ "$FAIL" = "0" ]
