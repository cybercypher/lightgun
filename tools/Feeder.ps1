# Console feeder, running the deployed copy at C:\games\feeder.
& "C:\games\feeder\GaimeFeeder.exe" 2>&1 | Tee-Object -FilePath "C:\games\feeder\logs\feeder.log"
