@echo off
echo ========================================
echo   SyncBeam Firewall Configuration
echo ========================================
echo.
echo This script requires Administrator privileges.
echo.

:: Check for admin rights
net session >nul 2>&1
if %errorlevel% neq 0 (
    echo ERROR: Please run this script as Administrator!
    echo Right-click and select "Run as administrator"
    pause
    exit /b 1
)

echo Adding firewall rules for SyncBeam...
echo.

:: Remove old rules if they exist
netsh advfirewall firewall delete rule name="SyncBeam TCP" >nul 2>&1
netsh advfirewall firewall delete rule name="SyncBeam UDP" >nul 2>&1
netsh advfirewall firewall delete rule name="SyncBeam mDNS" >nul 2>&1

:: Add TCP rule (for P2P connections)
echo [1/3] Adding TCP rule...
netsh advfirewall firewall add rule name="SyncBeam TCP" dir=in action=allow protocol=tcp localport=49152-65535 profile=private,domain description="SyncBeam P2P connections"

:: Add UDP rule (for general UDP if needed)
echo [2/3] Adding UDP rule...
netsh advfirewall firewall add rule name="SyncBeam UDP" dir=in action=allow protocol=udp localport=49152-65535 profile=private,domain description="SyncBeam P2P connections"

:: Add mDNS rule (port 5353 for discovery)
echo [3/3] Adding mDNS rule...
netsh advfirewall firewall add rule name="SyncBeam mDNS" dir=in action=allow protocol=udp localport=5353 profile=private,domain description="SyncBeam mDNS discovery"

echo.
echo ========================================
echo   Firewall rules added successfully!
echo ========================================
echo.
echo Now restart SyncBeam on both computers.
echo.
pause
