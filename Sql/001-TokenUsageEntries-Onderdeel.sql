-- Voegt de kolom Onderdeel toe aan de tokentabel: het onderdeel BINNEN een
-- applicatie dat een AI-aanroep deed (bijvoorbeeld "Daily chat", "Codevoorstel",
-- "Afbeelding", "Controle", "Zoeken"). Bestaande regels blijven ongewijzigd
-- staan en krijgen een lege waarde (NULL); het tokenoverzicht toont die als "-".
--
-- Veilig om vaker te draaien: doet niets als de kolom er al is. De broker
-- werkt ook zonder dit script (dan wordt het onderdeel niet vastgelegd), en
-- pakt de kolom op zodra hij bestaat - opnieuw opstarten is niet nodig.

USE JabasoftBase;
GO

IF COL_LENGTH('dbo.TokenUsageEntries', 'Onderdeel') IS NULL
BEGIN
    ALTER TABLE dbo.TokenUsageEntries ADD Onderdeel NVARCHAR(100) NULL;
    PRINT 'Kolom Onderdeel toegevoegd.';
END
ELSE
BEGIN
    PRINT 'Kolom Onderdeel bestond al.';
END
GO
