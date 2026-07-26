# Recherche - diagnostic et réparation du Registre Windows

## Position retenue

Ce moteur n'est pas un nettoyeur de registre. Il ne cherche pas les clés
"anciennes", "inutilisées" ou simplement inconnues. Une correction n'est
proposée que pour une valeur exacte, documentée par Microsoft et compatible
avec le build de Windows analysé.

## Sources principales

- Politique Microsoft sur les nettoyeurs de registre :
  https://support.microsoft.com/help/2563254
- Types de valeurs du registre :
  https://learn.microsoft.com/windows/win32/sysinfo/registry-value-types
- Vues 32/64 bits et redirection WOW64 :
  https://learn.microsoft.com/windows/win32/winprog64/registry-redirector
- Accès explicite à une vue alternative :
  https://learn.microsoft.com/windows/win32/winprog64/accessing-an-alternate-registry-view
- Sécurité et droits d'accès des clés :
  https://learn.microsoft.com/windows/win32/sysinfo/registry-key-security-and-access-rights
- Sauvegarde d'une ruche avec `RegSaveKeyEx` :
  https://learn.microsoft.com/windows/win32/api/winreg/nf-winreg-regsavekeyexw
- Chargement isolé d'une ruche de test avec `RegLoadAppKey` :
  https://learn.microsoft.com/windows/win32/api/winreg/nf-winreg-regloadappkeyw
- Montage en lecture seule d'une image Windows avec DISM :
  https://learn.microsoft.com/windows-hardware/manufacture/desktop/mount-and-modify-a-windows-image-using-dism?view=windows-11
- Énumération des index et éditions d'une image avec `Get-ImageInfo` :
  https://learn.microsoft.com/windows-hardware/manufacture/desktop/enable-or-disable-windows-features-using-dism?view=windows-11
- Export ciblé avec `reg export` :
  https://learn.microsoft.com/windows-server/administration/windows-commands/reg-export
- Autoruns Sysinternals et ses contrôles de signature/persistance :
  https://learn.microsoft.com/sysinternals/downloads/autoruns
- Clés `Run` et `RunOnce`, y compris les préfixes documentés `!` et `*` :
  https://learn.microsoft.com/windows/win32/setupapi/run-and-runonce-registry-keys
- IFEO peut légitimement lancer un débogueur pour une image donnée :
  https://learn.microsoft.com/windows-hardware/drivers/debugger/running-a-program-in-a-debugger
- AppInit DLLs est déconseillé et peut provoquer blocages et problèmes de
  performances, mais son existence seule ne prouve pas une infection :
  https://learn.microsoft.com/windows/win32/dlls/secure-boot-and-appinit-dlls
- Le Shell Windows peut légitimement être remplacé avec Shell Launcher sur
  certaines éditions Enterprise, Education et IoT :
  https://learn.microsoft.com/windows/configuration/shell-launcher/wesl-usersettingsetenabled
- Arbre documenté des services et valeurs `Type`, `Start` et `ImagePath` :
  https://learn.microsoft.com/windows-hardware/drivers/install/hklm-system-currentcontrolset-services-registry-tree
- Fusion des associations utilisateur et machine dans `HKEY_CLASSES_ROOT` :
  https://learn.microsoft.com/windows/win32/sysinfo/hkey-classes-root-key
- ProgID et associations de fichiers :
  https://learn.microsoft.com/windows/win32/shell/fa-progids
- Manuel Revo Uninstaller : seuls les éléments attribués au logiciel sont
  supprimés et les éléments effacés sont sauvegardés :
  https://www.revouninstaller.com/wp-content/themes/revo/files/RevoUninstallerProUserManual.pdf

## Décisions de sécurité

| Sujet | Décision v1 |
|---|---|
| Mode par défaut | Lecture seule |
| Vue du registre | Toujours explicite : 32 bits ou 64 bits |
| Suppression de sous-clé | Interdite |
| Prise de possession / modification ACL | Interdite |
| Valeur inconnue | Affichable comme inconnue, jamais corrigée |
| Chemin de fichier absent | Preuve insuffisante à lui seul |
| Source non Microsoft | Diagnostic possible, correction automatique interdite |
| Sauvegarde | Type exact, octets exacts, existence et SDDL avant écriture |
| Écriture | Une seule valeur documentée par transaction |
| Vérification | Relecture exacte de la valeur et contrôle du SDDL |
| Échec | Rollback immédiat puis vérification du rollback |
| Arrêt brutal | Transaction `Prepared` récupérée au prochain lancement |
| Annulation utilisateur | Refusée si la valeur a encore changé depuis la correction |
| Validation sans VM | Image Microsoft montée en lecture seule, ruches copiées puis chargées avec `RegLoadAppKey` |

## Limites explicites

- `RegSaveKeyEx` nécessite le privilège de sauvegarde. Il ne peut pas être le
  seul mécanisme d'annulation d'une correction unitaire.
- Une restauration de ruche complète est disproportionnée pour une valeur et
  peut échouer lorsque des sous-clés sont ouvertes.
- Un fichier `.reg` est utile pour une sauvegarde lisible, mais ne suffit pas
  comme preuve transactionnelle exacte ni comme sauvegarde d'ACL.
- Les mécanismes de persistance doivent être corrélés à la signature, au
  propriétaire, au chemin et au contexte. Un fichier absent ne prouve pas
  qu'une entrée peut être supprimée.

## Périmètre du laboratoire actuel

Le laboratoire valide le moteur de transaction sur un registre simulé et des
ruches Windows isolées. Il analyse aussi en lecture seule `Run`/`RunOnce`,
AppInit, IFEO `Debugger`, Winlogon, les services du `ControlSet` actif et les
associations de fichiers fusionnées utilisateur/machine. Ces constats
contextuels ne déclenchent aucune correction. Le laboratoire ne contient
encore aucune règle de correction destinée à un vrai PC. Cette séparation
évite qu'une règle incomplète devienne accidentellement active dans Tweakly.
