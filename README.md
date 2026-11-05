# Pricer live de stratégies optionnelles sur Deribit

Projet C# (.NET 10) : cotation en temps réel de structures optionnelles sur Deribit, options sur mesure
avec couverture, booking des trades, suivi du book, exports et enregistrement / rejeu des données.

L'interface est une application web locale (Blazor Server) : on la lance depuis VS Code et elle s'ouvre
dans le navigateur. Elle fonctionne de la même façon sous Windows et macOS.

## Prérequis

- SDK .NET 10 : tester dans le terminal -> `dotnet --version` (pour savoir si bonne version)
- VS Code avec l'extension **C# Dev Kit**
- Accès Internet vers `www.deribit.com` pour le mode live 

## Lancer l'application

Depuis le dossier du projet dans le terminal copier/coller une des commandes suivantes:

```bash
dotnet run --project src/DeribitPricer.App                                  # live
dotnet run --project src/DeribitPricer.App --launch-profile "Live + enregistrement" #live + enregistrement 
dotnet run --project src/DeribitPricer.App --launch-profile "Rejeu"         # rejeu du fichier configuré
```
Pour un rejeu accéléré, mettre par exemple `"ReplaySpeed": 10` dans `appsettings.json`.

Puis ouvrir http://localhost:5106 (le navigateur s'ouvre normalement tout seul).

Tests unitaires : `dotnet test` (31 tests).

## Live, enregistrement et rejeu 

Tout se règle dans `src/DeribitPricer.App/appsettings.json`, section `MarketData` :

 Clé | Rôle |
 `Mode` | `Live` ou `Replay` 
 `Currencies` | devises chargées en live (`BTC`, `ETH`) 
 `Record` | `true` : enregistre tous les messages reçus dans `data/recordings/deribit-<date>.jsonl.gz` 
 `ReplayFile` | fichier rejoué en mode `Replay` 
 `ReplaySpeed` | 1 = vitesse d'origine, 10 = dix fois plus vite, 0 = aussi vite que possible 
 `MaxRequestsPerSecond`, `MaxBurst` | limite de fréquence des appels Deribit 
 `SummaryPollSeconds` | période de rafraîchissement du résumé de toutes les options 

**Pour la démonstration** : lancer « Live + enregistrement » 15 à 30 minutes, arrêter avec Ctrl+C
(le fichier est fermé proprement), puis mettre le chemin du fichier créé dans `ReplayFile` et lancer « Rejeu ».

Trouver le fichier : dans l'explorateur de VS Code, ouvrir DeribitPricer/data/recordings/. Vous y trouverez un fichier du type 'deribit-20261015-140512.jsonl.gz'. 
/!\ La date et l'heure du nom sont en UTC, donc 2 heures de moins qu'à Paris. Vérifier que le fichier pèse plusieurs Mo (clic droit → « Révéler dans l'Explorateur de fichiers »). S'il fait 0 octet, c'est que l'arrêt s'est mal passé.
Indique ce fichier pour le rejeu : ouvre appsettings.json et remplace la ligne -> 
"ReplayFile": "data/replay/sample-btc.jsonl.gz",
par le nom de votre fichier (en gardant la virgule)

Enfin, ctrl+s et lancement du rejeu

## Écrans
**Contenu des differents ecrans de l'outil**

 **Marché** | options par devise, échéance puis strike : bid, ask, mark, vol implicite |
 **Pricer** | call/put spread, butterfly : prix théorique Black-76, écart au mark, vols implicites recalculées, prix exécutables, fourchette, grecques, profil de P&L, booking |
 **Sur mesure** | interpolation future / vol (annexe C), couverture delta-gamma-vega, prix client, booking |
 **Blotter** | trades, détail des jambes, débouclage partiel ou total, exports |
 **Book** | valeurs au mark et au théorique, P&L (réalisé / latent, BTC / USD), grecques par échéance, options réglées |
 **Flux** | mode, configuration, journal des connexions et erreurs |


