@echo off
rem Doppio clic: crea una licenza di TD Booking Suite. Serve Python con il pacchetto cryptography (pip install cryptography)
python "%~dp0td-licenze.py" %*
echo.
pause
