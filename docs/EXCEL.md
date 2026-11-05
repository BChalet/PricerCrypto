# Fonctions Excel 365 Windows

Le complément C# Excel-DNA expose `PRICEOPTION`, `VOLIMPLI` et `VOLSURFACE`.
Il appelle en lecture seule l'API de l'application Blazor : le même moteur C# et le même marché
sont utilisés depuis le navigateur et Excel. Aucun calcul financier n'est réécrit en VBA.

Le [classeur d'exemple](../examples/Excel/PricerCrypto.xlsx) contient les paramètres,
des formules de prix/vol et une matrice `VOLSURFACE`. Ses `#N/A` initiaux sont intentionnels :
charger le complément, démarrer l'application, choisir une maturité couverte puis changer le compteur de rafraîchissement.

## Installation

1. Installer le **SDK .NET 10** pour compiler le projet, et le **.NET 10 Desktop Runtime Windows**
   correspondant à l'architecture d'Excel (x64 pour Excel 64 bits, x86 pour Excel 32 bits).
2. Depuis la racine du dépôt, compiler :

   ```powershell
   dotnet build src/DeribitPricer.Excel -c Release
   ```

3. Dans `src/DeribitPricer.Excel/bin/Release/net10.0-windows/publish/`, choisir :
   - Excel 64 bits : `DeribitPricer.Excel-AddIn64-packed.xll` ;
   - Excel 32 bits : `DeribitPricer.Excel-AddIn-packed.xll`.

   Les deux versions sont générées ensemble. Le fichier `packed.xll` inclut les bibliothèques du complément.
   Vérifier l'architecture dans **Fichier → Compte → À propos d'Excel**.
4. Charger le fichier dans **Fichier → Options → Compléments → Gérer : Compléments Excel → Atteindre → Parcourir**.
   Si la politique de votre entreprise bloque les XLL téléchargés, faire approuver le complément par votre service informatique.
5. **Laisser l'application C# démarrée sur le même ordinateur qu'Excel** :

   ```powershell
   dotnet run --project src/DeribitPricer.App --launch-profile Live
   ```

   L'adresse par défaut est `http://localhost:5106/`. Pour un autre port local, définir la variable
   d'environnement `PRICER_API_URL` avant de démarrer Excel, par exemple `http://localhost:5200/`.
   Le complément accepte uniquement les adresses locales HTTP(S). Il ne contacte pas directement Deribit.
   Un serveur lancé dans le cloud n'est pas le `localhost` de votre PC : exécuter l'application sur Windows pour ce workflow.

Pour essayer sans connexion Deribit, générer le fichier simulé puis lancer le rejeu avec son chemin explicite :

```powershell
dotnet run --project tools/DeribitPricer.SampleGenerator -- data/replay/sample-btc.jsonl.gz 8
dotnet run --project src/DeribitPricer.App --no-launch-profile -- --urls http://localhost:5106 --MarketData:Mode=Replay --MarketData:ReplayFile=data/replay/sample-btc.jsonl.gz --MarketData:ReplaySpeed=0
```

Pour ce jeu simulé, le 27 novembre 2026 et un strike de 110 000 USD sont couverts.
Les valeurs du classeur d'exemple utilisent cette date ; en live, choisir une date encore cotée.

## Formules

Exemples pour un Excel français (`;` entre arguments, `,` pour les décimales).
Excel accepte `=PRICEOPTION(...)` et aussi la saisie `+PRICEOPTION(...)`.
Sur Excel anglais, utiliser `,` entre arguments et `.` dans les nombres.

Préparer les cellules :

| Cellule | Contenu | Convention |
|---|---|---|
| B2 | `BTC` | Sous-jacent présent dans le flux |
| B3 | une vraie date Excel | Choisir une échéance encore couverte par la nappe |
| B4 | `110000` | Strike en USD |
| B5 | `1` | Compteur de rafraîchissement |

Prix utilisant la vol et le future de marché :

```excel
=PRICEOPTION("Call";B3;B2;B4;;;;;B5)
```

Prix avec hypothèses manuelles (vol 50 %, **future** de l'échéance 112 000 USD) :

```excel
=PRICEOPTION("Put";B3;B2;B4;0,50;112000;"USD";;B5)
```

Vol implicite de marché (formater la cellule en **Pourcentage**) :

```excel
=VOLIMPLI(B2;B4;B3;;B5)
```

Nappe complète, déversée dans les cellules voisines (laisser assez de place) :

```excel
=VOLSURFACE(B2;;B5)
```

Signatures complètes :

```text
PRICEOPTION(optionType; maturity; underlying; strike; [volatility]; [forward]; [unit]; [snapshotId]; [refresh])
VOLIMPLI(underlying; strike; maturity; [snapshotId]; [refresh])
VOLSURFACE(underlying; [snapshotId]; [refresh])
```

- `optionType` : `Call` / `C` / `Put` / `P`.
- `maturity` : cellule date Excel au **calendrier 1900**, ou texte ISO `2027-03-26`.
  Une date sans heure vaut **08:00 UTC**, convention Deribit. Pour une heure explicite,
  utiliser un texte tel que `2027-03-26T08:00:00Z` ; les heures sans fuseau sont refusées.
  Ce paramètre est une date, **pas** un nombre d'années. Pour un classeur au calendrier 1904, utiliser le texte ISO.
- `volatility` : décimal (`0,50` = 50 %). Vide = vol interpolée de la nappe ; zéro = vol nulle.
- `forward` : prix du future de la maturité, **pas le spot**. Vide = future issu de la nappe.
  Une valeur manuelle nécessite une vol manuelle pour ne pas mélanger les scénarios.
- `unit` : `USD` par défaut ; `COIN` donne le prix USD divisé par le future,
  selon la convention déjà utilisée dans ce projet.
- `snapshotId` : vide pour l'état courant, sinon identifiant d'une capture de ce sous-jacent.
  Le prix historique utilise **l'horloge et les données de la capture**, pas celles d'aujourd'hui.
- `refresh` : cellule compteur facultative. **Changer sa valeur pour obtenir un nouvel état du marché**,
  notamment après une erreur de connexion. Les fonctions sont asynchrones et ne constituent pas un flux RTD continu.
  Employer le même compteur pour les différentes formules et utiliser un instantané pour figer une observation commune.

`VOLSURFACE` renvoie les échéances UTC en colonnes et les strikes en lignes, avec une ligne de futures.
La première cellule indique le sous-jacent, le mode (`Live` / `Replay`) et l'horodatage UTC.
Les vols sont en décimal, à formater en pourcentage dans le corps de la matrice.
Une case hors de la plage cotée renvoie `#N/A` : aucune extrapolation.

## Hypothèses et historique

Le modèle est **Black-76 à taux nul**, base ACT/365, pour **une unité** de sous-jacent.
Le prix est théorique, sans spread, frais ni marge. Il ne transmet aucun ordre.
La vol de marché est obtenue par inversion du mark (option OTM privilégiée), interpolation
linéaire en log-moneyness, puis en variance totale entre échéances. Cette interpolation
n'est pas une calibration imposant l'absence d'arbitrage global.

L'onglet **Volatilité** permet d'enregistrer des nappes. En live, des captures sont conservées
toutes les 300 secondes par défaut. Les fichiers sont dans `data/volatility/` ; conserver ce dossier
pour garder l'historique. La durée de conservation n'est pas limitée automatiquement : prévoir l'espace disque.
Les captures manuelles en rejeu sont explicitement étiquetées `Replay` et peuvent provenir de données simulées.

Pour connaître les identifiants disponibles :

```powershell
Invoke-RestMethod "http://localhost:5106/api/snapshots?underlying=BTC"
```

Mettre ensuite l'identifiant choisi en B6, par exemple :

```excel
=VOLIMPLI(B2;B4;B3;B6;B5)
=VOLSURFACE(B2;B6;B5)
```

Le projet ne télécharge pas de nappes historiques arbitraires depuis Deribit.
Pour comparer le 1er janvier au 1er octobre, il faut disposer d'observations de ces dates
(captures locales ou enregistrements réels rejoués puis capturés), avec des maturités et moneyness communes.
La volatilité historique réalisée n'est pas substituée à une nappe implicite manquante.

## API HTTP C#

L'API est locale, sans authentification, et ne doit pas être exposée telle quelle sur Internet.
Toutes les routes sont en lecture seule et renvoient du JSON sans cache HTTP :

| Route GET | Paramètres |
|---|---|
| `/api/priceoption` | `optionType`, `maturity`, `underlying`, `strike`, optionnels `volatility`, `forward`, `unit`, `snapshotId` |
| `/api/volimpli` | `underlying`, `strike`, `maturity`, optionnel `snapshotId` |
| `/api/volsurface` | `underlying`, optionnel `snapshotId` |
| `/api/snapshots` | `underlying` |

Une saisie invalide ou une zone hors couverture renvoie HTTP 400 avec `error` ;
un identifiant absent ou d'un autre sous-jacent renvoie HTTP 404.
Le complément affiche `ERREUR PRICER : ...` pour ces erreurs ; une API arrêtée n'est jamais remplacée par un faux prix.
Les captures se font dans l'interface, pas par une requête GET susceptible d'être déclenchée par un recalcul Excel.

## Validation

```powershell
dotnet build DeribitPricer.sln
dotnet test tests/DeribitPricer.Tests
```

Les tests couvrent le pricing, l'interpolation, les bornes, la persistance, les dates,
la matrice Excel et les appels du client C# vers l'API réelle dans un serveur de test.
Les XLL 32/64 bits peuvent être générés sous Linux ; leur chargement dans Excel,
le déversement et le rafraîchissement doivent aussi être vérifiés sur Excel Windows.
