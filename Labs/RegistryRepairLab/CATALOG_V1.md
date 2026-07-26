# Catalogue v1 - règles d'admission

Une règle de correction ne peut entrer dans le catalogue produit que si elle
contient tous les éléments suivants :

1. Identifiant stable et version de la règle.
2. Build Windows minimal et maximal vérifiés.
3. Éditions Windows compatibles.
4. Ruche, chemin, nom et vue 32/64 bits explicites.
5. Type exact et octets attendus.
6. Lien Microsoft décrivant cette valeur et sa valeur attendue.
7. Cas où la valeur peut légitimement différer.
8. Test valide, absent, mauvais type et mauvaise donnée.
9. Test de refus d'accès, échec d'écriture et rollback.
10. Validation sur une ruche isolée, sur les corpus officiels des builds et
    éditions visés, puis sur un PC physique si le comportement dépend de
    services ou d'une session Windows active.
11. Signature RSA-PSS valide du catalogue avec une clé de publication séparée.

## Classes d'analyse prévues

| Classe | Analyse | Correction automatique v1 |
|---|---|---|
| Valeur Windows documentée | Type et donnée exacts | Oui, après validation de build |
| Winlogon modifié | Écart, signature, contexte | Non par défaut |
| Démarrage automatique cassé | Cible, signature, propriétaire, contexte | Non par défaut |
| Service avec chemin absent | SCM, image, signature, état, package | Non |
| IFEO / AppInit / LSA | Persistance et détournement | Non |
| Association de fichier | État et origine | Non |
| Résidu de désinstallation | Preuve d'appartenance à l'installeur | Non sans manifeste |
| Valeur inconnue | Inventaire uniquement | Non |

Le catalogue de production reste vide tant qu'une première règle n'a pas son
corpus Microsoft complet et ses tests sur Windows 11 pris en charge.
