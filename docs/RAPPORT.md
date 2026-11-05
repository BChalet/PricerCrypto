# Pricer live de stratégies optionnelles sur Deribit — Rapport

*Groupe : CHALET Benjamin - MELE Milena — C# pour la finance — rendu du 12/11/2026*

> Trame de rapport. Les parties marquées **[À compléter]** doivent être remplies avec vos propres
> résultats, obtenus sur un vrai enregistrement Deribit (captures d'écran, chiffres).

## 1. Objectif et périmètre

L'outil cote en temps réel des structures optionnelles simples sur Deribit (call spread, put spread,
butterfly en calls ou en puts), cote et couvre des options sur mesure, enregistre les trades dans un
blotter et suit le P&L et le risque du book. Il fonctionne en live sur l'API publique de Deribit ou en
rejeu d'un fichier enregistré.

Toutes les parties du sujet sont traitées :

| Partie | Réalisation |
|---|---|
| 1. Référentiel | écran *Marché* : choix de la devise (BTC, ETH), parcours par échéance puis strike, bid / ask / mark / vol |
| 2.1 Structures | écran *Pricer*, validation des saisies avec messages explicites |
| 2.2 Prix et grecques | Black-76 et inversion maison, écarts signalés, prix exécutables, fourchette, grecques comparées à Deribit |
| 2.3 Profil | grille paramétrable (−30 % à +30 % par pas de 5 % par défaut), échéance et aujourd'hui, gain / perte max, points morts |
| 2.4 Couverture | écran *Sur mesure* : interpolation annexe C, couverture gamma-vega puis delta, arrondis, coût, prix client |
| 3. Booking et book | blotter persistant, débouclage partiel, book en continu, règlement des échus, exports CSV et Excel |
| 4. Enregistrement / rejeu | enregistrement JSON Lines compressé, rejeu à vitesse d'origine ou accélérée, choix par configuration |

## 2. Architecture

### 2.1 Vue d'ensemble

La solution suit le découpage vu en cours (application / bibliothèque métier / tests) :

```
                ┌────────────────────────── DeribitPricer.Core ───────────────────────────┐
 Deribit REST ─►│ LiveMarketFeed ─┐                                                       │
 Deribit WS   ─►│                 ├─► MarketEvent ─► DeribitMessageParser ─► MarketDataStore│
 Fichier      ─►│ ReplayMarketFeed┘        │                                   │          │
                │                          └─► MarketRecorder (fichier)        ▼          │
                │   StructureBuilder ─► StructurePricer ◄── OptionAnalytics ◄── Black76   │
                │   CustomOptionPricer ─► HedgeCalculator      ImpliedVolSolver           │
                │   TradeFactory ─► Blotter ─► BookCalculator ─► BookExporter            │
                └──────────────────────────────────────────────────────────────────────────┘
                                                ▲
                ┌──────── DeribitPricer.App (Blazor Server) ───────┐
                │ Marché · Pricer · Sur mesure · Blotter · Book    │  rafraîchissement périodique
                └──────────────────────────────────────────────────┘
```

- **DeribitPricer.Core** (bibliothèque de classes) contient toute la logique : aucun code financier
  dans l'interface. C'est ce qui permet de la tester unitairement.
- **DeribitPricer.App** (Blazor Server) : l'interface est une page web servie localement. Choix fait
  parce qu'il fonctionne sous Windows comme sous macOS (WPF / WinForms sont limités à Windows), se
  développe entièrement dans VS Code, et parce que le rendu côté serveur permet de rafraîchir les écrans
  en continu sans écrire de JavaScript.
- **DeribitPricer.Tests** (MSTest) : 31 tests unitaires.

### 2.2 Données de marché : un seul chemin pour le live et le rejeu

Le point clé de l'architecture est que **toutes** les données passent par un message unique,
`MarketEvent` (horodatage de réception, source REST ou WebSocket, nom de la méthode ou du canal,
paramètres, contenu JSON brut). En live, chaque réponse REST et chaque notification WebSocket devient un
`MarketEvent`, appliqué au store par `DeribitMessageParser` et, si l'enregistrement est actif, écrit tel
quel dans le fichier. En rejeu, `ReplayMarketFeed` relit ces mêmes messages et les donne **au même
parseur**, en respectant les délais d'origine divisés par le facteur de vitesse. Le reste de l'outil ne
sait pas d'où viennent les données : le comportement est identique par construction.

L'horloge de marché suit la même logique : heure système en live, horodatage du dernier message en
rejeu. Les maturités, les horodatages des trades et la détection des échéances passées utilisent cette
horloge, si bien qu'un rejeu reproduit aussi le règlement des options échues.

Le choix live / rejeu se fait par configuration (`appsettings.json`, profil de lancement ou argument
`--MarketData:Mode=Replay`), sans modification du code. `MarketDataService` construit le bon flux
(interface `IMarketDataFeed`) au démarrage.

### 2.3 Utilisation de l'API Deribit et limites de fréquence

| Besoin | Point d'accès | Fréquence |
|---|---|---|
| Référentiel options et futures | `public/get_instruments` | démarrage, toutes les 10 min, chaque minute juste après 08:00 UTC |
| Bid, ask, mark, vol, future de toutes les options | `public/get_book_summary_by_currency` | toutes les 5 s (un seul appel par devise) |
| Ticker complet (bid_iv, ask_iv, grecques) | WebSocket `ticker.{instrument}.100ms` | en continu, seulement pour les instruments suivis |
| Futures (couverture delta) | WebSocket `ticker.{future}.100ms` | en continu |
| Indice | WebSocket `deribit_price_index.btc_usd` (repli REST `get_index_price`) | en continu |
| Prix de règlement | `public/get_delivery_prices` | avec le référentiel |

Le résumé global évite d'interroger le ticker de chacune des centaines d'options. Les tickers complets
ne sont demandés que pour les instruments réellement utilisés : jambes de la structure cotée,
couvertures, positions du book (abonnements ajoutés et retirés dynamiquement). Quand un ticker récent
existe, il a priorité sur le résumé (plus riche et plus frais).

Toutes les requêtes (REST et requêtes WebSocket) passent par un **limiteur à seau à jetons**
(`TokenBucketRateLimiter`, 5 requêtes/s par défaut, configurable). En cas de réponse « too many
requests » (HTTP 429 ou code 10028), la requête est retentée après une attente exponentielle. La
connexion WebSocket utilise le heartbeat Deribit et se reconnecte automatiquement en se réabonnant.

### 2.4 Enregistrement

Format JSON Lines (un `MarketEvent` par ligne), compressé en gzip : environ 10 fois plus petit, ce qui
compte puisque le résumé de toutes les options représente plusieurs centaines de kilo-octets toutes les
5 secondes. L'écriture se fait sur une tâche dédiée (file `Channel`) pour ne jamais ralentir la
réception ; le fichier est fermé proprement à l'arrêt de l'application (Ctrl+C). Un fichier tronqué
reste rejouable jusqu'au dernier message lisible.

## 3. Modèle et conventions

### 3.1 Black-76 et options inverses

Implémentation de l'annexe A : taux nul, `T = (échéance − horodatage de la cotation) / (365 × 24 × 3600)`,
`F` = champ `underlying_price` (future de l'échéance), vol = `mark_iv / 100`. Le prix Deribit en crypto
est le prix en USD divisé par F. La loi normale est calculée avec l'algorithme de Hart (précision double).

### 3.2 Vol implicite

Newton à partir de 50 %, arrêt à 10⁻⁶ USD d'écart, bascule sur une dichotomie dans [0,01 % ; 500 %] si le
vega devient trop petit ou si σ sort de l'intervalle. Le prix de marché (bid, mark, ask) est converti en
USD avant inversion, et refusé s'il n'est pas strictement entre ses bornes de non-arbitrage : la vol est
alors affichée « incalculable » avec la raison.

### 3.3 Grecques

Formules de l'annexe B, calculées sur le prix en USD : delta en unités de sous-jacent, gamma pour 1 USD,
vega en USD par point de vol (÷ 100), theta en USD par jour calendaire (÷ 365). Les grecques d'une
structure et du book sont la somme des grecques unitaires pondérées par les quantités signées.

### 3.4 Structures

Les structures sont définies « à l'achat », la vente inverse tous les signes :

| Structure | Achat |
|---|---|
| Call spread | +1 call K1, −1 call K2 (K1 < K2) |
| Put spread | +1 put K2, −1 put K1 (K1 < K2) |
| Butterfly | +1 K1, −2 K2, +1 K3, avec K2 − K1 = K3 − K2 |

Sont refusés avec un message explicite : échéance ou strike non cotés, strikes dans le mauvais ordre,
butterfly non symétrique, quantité nulle, inférieure au minimum ou non multiple du pas de l'instrument.

**Prix exécutable** : à l'achat on paie l'ask des jambes achetées et on reçoit le bid des jambes vendues,
inversement à la vente. Sans bid ou sans ask sur une jambe, la structure est « non traitable » et le
bouton de booking est désactivé. **Fourchette** = prix d'achat − prix de vente.

### 3.5 Profil de P&L

- À l'échéance : payoff − prime payée. La prime (en BTC, au prix exécutable, ou au théorique si la
  structure n'est pas traitable) est convertie en USD au future actuel. Le P&L en BTC est aussi affiché
  (payoff USD / sous-jacent − prime BTC).
- Aujourd'hui : chaque jambe est réévaluée avec Black-76, à vol et maturité inchangées, le future étant
  remplacé par le point de la grille.
- Gain maximal, perte maximale et points morts sont calculés **exactement** et non lus sur la grille :
  le payoff est linéaire par morceaux, coudé aux strikes. On évalue le payoff en 0 et à chaque strike, la
  pente au-delà du dernier strike (somme des quantités de calls) indique un gain ou une perte illimités,
  et les points morts sont les zéros de chaque segment.

### 3.6 Option sur mesure

Annexe C : future interpolé linéairement en temps entre les deux échéances encadrantes ; log-moneyness
k = ln(K / F(T)) ; sur chaque échéance encadrante, strike équivalent Ki = Fi·e^k et interpolation linéaire
en log-moneyness entre les deux strikes cotés qui l'encadrent ; enfin variance totale σ²T interpolée
linéairement en temps. Si l'échéance est cotée, seule l'étape 3 s'applique.

Les vols σa et σb sont **recalculées par notre inversion** à partir du mark (pas lues dans `mark_iv`).
Pour chaque strike, on prend l'option hors de la monnaie (put sous le future, call au-dessus), dont le
prix est le plus sensible à la vol et donc le plus précis à inverser.

Refus : échéance passée ou hors de [première ; dernière] échéance cotée, strike équivalent hors de la
plage des strikes cotés d'une échéance encadrante.

L'échéance d'une option sur mesure est fixée à 08:00 UTC, comme celles de Deribit : cela permet de la
régler avec le prix de règlement Deribit du jour.

### 3.7 Couverture et choix de la paire

Système de l'annexe C : deux options cotées annulent gamma et vega, puis un future (delta 1 par unité
de sous-jacent) annule le delta.

**Pourquoi deux échéances différentes.** Avec les formules de l'annexe B,

  Γ / 𝒱 = [n(d1) / (Fσ√T)] / [F n(d1) √T] = 1 / (F² σ T).

Le rapport gamma/vega ne dépend que de la maturité (et de la vol). Deux options de **même échéance**
ont donc presque le même rapport : les deux équations du système sont quasiment proportionnelles, le
déterminant Γ1𝒱2 − Γ2𝒱1 est proche de 0 et les quantités explosent (exactement 0 pour un call et un
put de même strike). Il faut deux maturités différentes : une option courte (riche en gamma par
unité de vega) et une option longue (riche en vega).

**Paire proposée par défaut** (`HedgePairSuggester`) : une option sur chacune des deux échéances qui
encadrent l'option sur mesure (si elle tombe sur une échéance cotée : cette échéance et la suivante),
de même type, au strike coté le plus proche du strike équivalent (même log-moneyness), et ayant un bid
et un ask. Les deux options ressemblent ainsi le plus possible à l'option couverte : la couverture reste
bonne quand le marché bouge, et les quantités restent raisonnables. Future : la première échéance
cotée après l'option sur mesure (même sous-jacent que la période couverte), sinon le perpétuel.

**Refus d'une paire** : l'utilisateur peut choisir n'importe quelle paire. L'outil calcule le
déterminant relatif |det| / (|Γ1𝒱2| + |Γ2𝒱1|) et refuse la paire en dessous de 2 %, avec un message
expliquant pourquoi. Il refuse aussi deux fois la même option, ou une option sans cotation.

**Arrondis** : les quantités d'options sont arrondies au pas de cotation (0,1), puis la quantité de
future est calculée avec ces quantités arrondies et arrondie à son tour. Les futures Deribit étant
inverses (quantité en USD, contrats de 10 USD sur BTC), le pas en BTC vaut 10 / F. L'écran affiche les
grecques de l'option, de la couverture, de la position globale après arrondi (résidus) et avant
arrondi (nulles, ce qui vérifie la résolution).

**Coût de couverture** : Σ |q| × |prix exécutable − mid|, en USD. **Prix client** : prix théorique, plus
(si le client achète) ou moins (s'il vend) le coût de couverture par option et une marge desk
paramétrable. Le desk ne traite que s'il a une couverture valide : le bouton de booking n'apparaît
qu'une fois la couverture calculée.

### 3.8 Booking, débouclage et book

Un trade enregistre l'identifiant, l'horodatage, la structure, le sens, la quantité, et pour chaque
jambe l'instrument, la quantité signée, le prix d'exécution, le future et la vol au moment du trade,
ainsi que la prime totale en BTC. Le blotter est sauvegardé en JSON à chaque ajout (fichier séparé en
rejeu).

Un **débouclage** est un nouveau trade lié au trade d'origine (`ParentTradeId`) : chaque jambe est
retournée au prorata de la quantité débouclée, au prix exécutable du moment (prix théorique pour une
option sur mesure). On ne peut pas déboucler plus que la quantité ouverte.

Le **book** rejoue les exécutions de chaque structure dans l'ordre, instrument par instrument, au
**prix de revient moyen** : réduire une position (débouclage, règlement) réalise le P&L correspondant,
le reste est latent. Ainsi P&L depuis l'ouverture = réalisé + latent = valeur actuelle − primes nettes
payées. Le book affiche la valeur au mark Deribit et au prix théorique, le P&L en BTC et en USD
(converti à l'indice), les grecques par échéance et totales.

**Règlement** : quand l'horloge dépasse l'échéance d'une jambe et que Deribit a publié le prix de
règlement de cette date, la jambe est fermée à son payoff (payoff USD / prix de règlement, en BTC) : elle
sort du book et le montant reste dans le P&L réalisé. Avant publication, elle est affichée « en attente
de règlement » et estimée sur l'indice.

Conventions particulières : une option sur mesure n'a pas de mark Deribit, son mark est son prix
théorique interpolé ; un future de couverture est suivi en USD (P&L = q × (F − F d'exécution), delta 1
comme dans l'annexe) et converti en BTC à l'indice.

### 3.9 Exports

À tout moment depuis le Blotter ou le Book : CSV (séparateur « ; » et décimales à la française pour
s'ouvrir directement dans Excel FR, UTF-8 avec BOM) et classeur Excel (bibliothèque ClosedXML) avec
les onglets Totaux, Structures, Positions, Grecques et Blotter.

## 4. Tests

Pour vérifier que l'outil calcule juste, nous avons écrit 31 tests unitaires avec MSTest, regroupés
dans le projet `DeribitPricer.Tests`. Un test unitaire est une petite fonction qui appelle une partie
du code avec des données connues et vérifie automatiquement que le résultat est celui attendu : par
exemple, qu'un call et un put calculés avec notre Black-76 respectent la parité call-put, ou qu'une vol
implicite retrouvée à partir d'un prix redonne bien ce prix. Les tests portent uniquement sur le moteur
(`DeribitPricer.Core`) et s'appuient sur un petit marché fictif dont on connaît tous les prix : ils
tournent en quelques secondes, sans connexion à Deribit ni interface. Ils servent à deux choses :
s'assurer que chaque brique (formules, vol implicite, validation des saisies, couverture, book,
enregistrement et rejeu) fait ce que demande le sujet, et détecter immédiatement une erreur si l'on
modifie le code par la suite. Ils se lancent avec la commande `dotnet test` et doivent tous réussir.

Les 31 tests couvrent notamment :

- loi normale sur des valeurs de référence ; Black-76 à la monnaie (forme fermée) ; parité call-put ;
- grecques comparées à des différences finies ;
- vol implicite : aller-retour prix → vol → prix sur une grille de strikes, vols et maturités, refus hors
  bornes, bascule sur la dichotomie pour une option très en dehors de la monnaie ;
- structures : composition des jambes, chaque cas de saisie refusée, prix exécutables, non traitable ;
- profil : gain et perte max et points morts d'un call spread, d'un call vendu (perte illimitée),
  d'un butterfly (deux points morts) ;
- option sur mesure : vol de marché retrouvée sur un strike et une échéance cotés, interpolation du
  future et de la variance totale, refus hors plage ;
- couverture : grecques globales nulles avant arrondi, quantités au pas, refus d'une paire de même
  échéance, prix client ;
- book : prime, P&L = valeur − prime, P&L réalisé d'un débouclage partiel, règlement d'échus ;
- données : lecture des messages Deribit, priorité du ticker sur le résumé, enregistrement puis rejeu,
  respect du limiteur de fréquence ; persistance du blotter ; exports.

## 5. Résultats [À compléter]

À illustrer par des captures prises en rejeu d'un enregistrement réel (préciser la date et la durée).

1. **Référentiel** : capture de l'écran Marché.
2. **Black-76 contre le mark Deribit** : écarts observés (ordre de grandeur, cas où l'écart dépasse
   0,0001 BTC). Pistes d'explication : Deribit calcule le mark avec son propre horodatage et son propre
   future ; arrondis ; options très courtes.
3. **Vols implicites** : écarts entre notre inversion et bid_iv / mark_iv / ask_iv. Cas des options très
   en dehors de la monnaie : le pas de cotation de 0,0001 BTC représente une grande variation de vol,
   d'où des écarts signalés ; cas des vols incalculables (prix sous la valeur intrinsèque, etc.).
4. **Grecques** : comparaison à Deribit (unités : vega par point, theta par jour).
5. **Une structure cotée** : prix, fourchette, profil de P&L et commentaire.
6. **Une option sur mesure** : détail de l'interpolation, couverture proposée, résidus après arrondi,
   coût, prix client. Montrer aussi le refus d'une paire de même échéance.
7. **Book** : quelques trades, un débouclage partiel, P&L, grecques par échéance, une option réglée.
8. **Rejeu** : même comportement qu'en live, à vitesse 1 et accélérée.

## 6. Limites et pistes d'amélioration

- Taux nul (comme demandé) ; le future absorbe le portage.
- La couverture delta suppose un future de delta 1 par unité, alors qu'un future inverse a un delta en
  BTC légèrement différent.
- Le coût de couverture ne tient pas compte de la profondeur du carnet (`get_order_book`) : pour de
  grosses quantités, il faudrait consommer plusieurs niveaux de prix.
- En rejeu, seuls les instruments suivis pendant l'enregistrement ont des tickers complets (bid_iv,
  ask_iv, grecques Deribit) ; les autres ont les données du résumé.
- Pas de gestion multi-utilisateur ni de base de données (blotter en fichier JSON).
