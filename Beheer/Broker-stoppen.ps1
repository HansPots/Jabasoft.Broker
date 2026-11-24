# Stopt Jabasoft.Broker, hoe hij ook gestart is (aanmeldtaak of Jabasoft).
# Nodig om een nieuwe versie te bouwen: een draaiende broker houdt zijn
# bestanden in bin vast. Daarna weer starten met "Broker starten" of door
# Jabasoft te openen.

Get-Process -Name 'Jabasoft.Broker' -ErrorAction SilentlyContinue | Stop-Process -Force
