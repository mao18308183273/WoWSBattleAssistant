@echo off
REM 战舰数据一键更新 - 不需要 APK，不需要手机，双击即用。
REM 流程：先花 1 秒体检，有新船才抓全部，没有就直接告诉你已是最新。
REM 编码：GBK + CRLF + 无 BOM。任何一步出错都会停住并显示原因，绝不静默关闭。

chcp 936 >nul
setlocal

set "DIR=%~dp0"
set "PY=C:\Users\mao_z\AppData\Local\Programs\Python\Python312\python.exe"

REM 绝对路径不存在时，退回 PATH 上的 python - 换机器或重装 Python 也能用
if not exist "%PY%" set "PY=python"

echo ============================================================
echo   360 战舰助手 - 舰船数据一键更新
echo ============================================================
echo.

echo [1/2] 检查 Python ...
"%PY%" -c "import sys;sys.exit(0 if sys.version_info[0]==3 else 1)"
if errorlevel 1 goto NOPY
echo       OK
echo.

REM 带 force 参数则跳过体检，直接全量重抓
if /i "%~1"=="force" goto DO_UPDATE

echo [2/2] 先体检 - 看看服务端有没有新船 ...
echo.
"%PY%" "%DIR%wows_ship_scraper.py" --check
if errorlevel 10 goto DO_UPDATE

echo.
echo 本地数据已是最新，不需要更新。
echo 想强制重抓一次，可以带 force 参数运行本脚本。
echo.
pause
exit /b 0

:DO_UPDATE
echo.
echo 开始抓取全部舰船 - 约 6 分钟，请耐心等待 ...
echo.
"%PY%" "%DIR%wows_ship_scraper.py" --update-index
if errorlevel 1 goto FAIL

echo.
echo ============================================================
echo   完成。中文舰名索引已更新到:
echo   C:\Users\mao_z\Downloads\WoWSBattleAssistant\Tools\ship_names_zh.json
echo ============================================================
echo.
pause
exit /b 0

:NOPY
echo.
echo [错误] 没找到可用的 Python 3:
echo   %PY%
echo.
echo 请确认已安装 Python 3，或修改本脚本里的 PY 变量指向你的 python.exe。
echo.
pause
exit /b 1

:FAIL
echo.
echo [错误] 抓取失败 - 详见上方输出。
echo.
echo 绝大多数情况是网络不通，稍后重试即可。
echo 若上方出现"签名被服务端拒绝"，说明 360 换了签名盐，
echo 这时才需要去官网下载最新 APK，然后这样运行:
echo   python wows_ship_scraper.py --apk 新APK路径 --update-index
echo.
pause
exit /b 1
