# RegistryRepairLab

Laboratoire interne au dépôt Tweakly pour construire le moteur de diagnostic
et de réparation du registre sans exposer une règle incomplète dans le logiciel.

## Exécuter les tests

```powershell
dotnet run --project .\RegistryRepair.Tests\RegistryRepair.Tests.csproj
dotnet run --project .\RegistryRepair.WindowsTests\RegistryRepair.WindowsTests.csproj
```

Résultat actuel : 42/42 tests moteur/catalogue/analyse contextuelle et 10/10 tests avec les API
Windows. Tous les projets sont compilés en Release avec les avertissements
traités comme des erreurs.

Les analyseurs `Run`/`RunOnce`, AppInit, IFEO `Debugger`, Winlogon, services et
associations de fichiers sont opérationnels en lecture seule. Ils signalent
uniquement les données mal formées ou à examiner et ne proposent aucune
correction automatique.

## Corpus Windows 11 sans virtualisation

Le collecteur accepte un `install.wim` ou `install.esd` officiel et un index
d'édition. Il monte l'image avec DISM en lecture seule, copie uniquement les
ruches `SOFTWARE`, `SYSTEM`, `DEFAULT` et `NTUSER.DAT`, calcule leur SHA-256,
lit le build et l'édition, puis démonte l'image avec `/Discard`.

```powershell
.\Collect-OfflineWindowsCorpus.ps1 `
  -ImagePath 'D:\sources\install.wim' `
  -Index 6 `
  -OutputRoot 'D:\Tweakly-Registry-Corpus'
```

Le script exige les droits administrateur uniquement pour DISM. Il ne modifie
ni l'image source, ni le registre du PC hôte. Aucun corpus réel n'est encore
présent dans ce dossier.

Tweakly référence uniquement les bibliothèques nécessaires à la page
expérimentale d'analyse en lecture seule. Le moteur de correction, le catalogue
de règles et la chaîne de corpus ne sont pas exposés dans l'interface. Le
démarrage et le système de mise à jour de Tweakly ne sont pas modifiés.
