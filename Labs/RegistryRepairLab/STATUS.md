# État du chantier au 18/07/2026

## Terminé

- Recherche Microsoft initiale et comparaison Autoruns/Revo.
- Cartographie des accès registre existants de Tweakly.
- Modèle d'adresse avec vue 32/64 bits obligatoire.
- Conservation exacte du type et des octets d'une valeur.
- Capture et vérification du descripteur de sécurité de la clé.
- Journal durable, versionné et contrôlé par SHA-256 avant utilisation.
- Vérification après écriture.
- Rollback immédiat et vérifié en cas d'échec.
- Récupération d'une transaction interrompue brutalement.
- Annulation individuelle refusée si la valeur a changé depuis.
- Verrou de correction par adresse registre.
- Sauvegarde complète des valeurs de la clé et contrôle des valeurs voisines.
- Catalogue signé RSA-PSS avec clé privée absente du logiciel.
- Backend Windows réel testé sur des ruches `RegLoadAppKey` isolées.
- Lecteur d'images Windows 11 hors ligne avec identification build/UBR/édition.
- Collecteur DISM en lecture seule pour `install.wim` et `install.esd`.
- Analyseurs contextuels en lecture seule pour `Run`/`RunOnce`, AppInit,
  IFEO `Debugger`, Winlogon, services et associations de fichiers.
- Énumération des sous-clés avec les seuls droits de lecture nécessaires.
- Résolution du `ControlSet` actif avant l'analyse des services.
- Fusion des associations utilisateur/machine avec priorité à l'utilisateur.
- 42/42 tests du moteur, du catalogue et des analyseurs contextuels.
- 10/10 tests avec les API Windows et les ruches hors ligne.
- Builds Release : 0 erreur, 0 avertissement.
- Page Tweakly expérimentale intégrée sous Maintenance, en lecture seule.

## Garde-fous actifs

- Aucune suppression de sous-clé.
- Aucune création automatique d'une clé absente.
- Aucune modification d'ACL ni prise de possession.
- Aucune correction issue d'une source non Microsoft.
- Aucune règle de correction réelle livrée pour l'instant.
- Aucun bouton ni chemin d'exécution permettant une correction depuis Tweakly.
- Seuls les analyseurs contextuels en lecture seule sont référencés par Tweakly.

## Étapes restantes avant toute correction automatique

1. Constituer le premier corpus de règles Microsoft par build Windows 11.
2. Corréler fichier, signature, éditeur, service et mécanisme d'installation.
3. Constituer les corpus officiels Windows 11 par build et édition avec DISM.
4. Tester chaque règle sur des copies propres puis altérées de ces ruches.
5. Valider sur plusieurs PC physiques les règles dépendantes de l'exécution.
6. Faire auditer le catalogue avant d'activer une correction dans Tweakly.

Le diagnostic contextuel est exposé en lecture seule dans une page
expérimentale. Le moteur de correction et la chaîne de corpus restent réservés
à la recherche : aucune règle réelle de correction n'a encore été admise dans
le catalogue.
