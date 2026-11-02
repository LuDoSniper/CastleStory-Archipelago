# Castle Story × Archipelago — v0.6.7 (transfert de campagne de secours)

Castle Story (Unity 5.6 / BepInEx 5) ↔ Archipelago ↔ ArchiLAN.

## v0.6.7 — Transfert texte de secours pour Unity 5.6

Le bridge produit désormais `campaign_wire.txt`, un instantané atomique et versionné de la campagne, indépendant de `JsonUtility`. Chaque mission utilise une ligne à sept champs, les chaînes UTF-8 sont encodées en Base64, et Unity recoupe le jeton, le protocole et le nombre de missions avant de remplacer la campagne. Le fichier JSON reste disponible pour les diagnostics. Le fichier `session.json` est publié en dernier. Un manifeste obsolète ou tronqué est ignoré sans autoriser un lancement libre.

Pour tester : installer le kit v0.6.7, fermer puis relancer le jeu, reconnecter le même slot/seed ; **inutile de régénérer la seed**. Dans `BepInEx/LogOutput.log`, chercher `[AP MANIFEST]` : le résultat attendu contient `Transfert texte V1` et le nombre exact de missions. Si l'interface indique une autre erreur, fournir la ligne `[AP MANIFEST]` et les informations de diagnostic affichées (sans `connection.json`).

La CI ne simule pas le moteur Unity ; le démarrage réel d'une mission doit encore être testé dans Castle Story.

## v0.6.6 — Le manifeste arrive dans le bridge, mais pas dans Unity

Diagnostic confirmé par capture d'écran utilisateur : seed neuve, slot
Invasion, protocole 6, bridge 0.6.5, `slot_data` et champ `campaign`
effectivement reçus. Ce n'est **pas** une absence de connexion, ni une
preuve de map manquante ; c'est le trajet **bridge → Unity** qui manque
de visibilité ou de robustesse.

Le bridge 0.6.6 produit maintenant deux fichiers atomiques :
`session.json` (connexion et diagnostics) et
`campaign_view.json` (manifeste plat, missions au niveau racine, token
de la session, nombre attendu de missions). Unity 5.6 lit le manifeste
plat et le recoupe avec le token actif. Cela évite de dépendre
uniquement de la désérialisation imbriquée
`session.campaign.missions` de l'ancien code.

En cas d'échec, l'écran indique le nombre de missions fourni par le
bridge, celui réellement décodé par Unity, le statut du manifeste et
la cause du décodage. Le fichier `BepInEx/LogOutput.log` contient
`[AP MANIFEST]` avec le même diagnostic. Aucune donnée sensible ne
doit être copiée depuis `connection.json`.

**Pour ce correctif, ne remplace pas l'APWorld et ne régénère pas une
seed fonctionnelle.** Utilise l'installateur `Install-CastleStoryAP.ps1`
du dernier kit, puis reconnecte-toi à la même session Invasion. Le
bridge et le mod doivent tous les deux afficher la v0.6.6.

La CI teste les fichiers et les contrats de transfert, pas
l'exécution en conditions réelles de `JsonUtility` dans Castle Story :
cette validation en jeu reste nécessaire.

## v0.6.5 : corriger les clics traversants et la session fantôme

**Observé sur un test réel :** le menu IMGUI était visible et les clics
passaient parfois au menu natif, même après les correctifs v0.6.3/0.6.4.
La v0.6.5 intercepte le clic \`Fermer\` dans \`Update()\` via
\`Input.GetMouseButtonDown\` et suspend les Unity \`EventSystem\` natifs
pendant la popup. Elle maintient la protection jusqu'après le relâchement
de la souris. Il faut **confirmer le résultat en jeu** : CI verte n'est pas
une preuve que le Unity 5.6 utilisé par Castle Story distribue les inputs
comme prévu.

La même version exige un heartbeat \`session.updated_at\` du client réseau.
Un ancien \`session.json\` avec \`connected=true\` ne doit plus donner
l'illusion d'une connexion active. L'UI doit demander de mettre à jour
le bridge si le protocole de diagnostic est absent.

### Installation atomique recommandée sur Windows

1. Télécharger l'artifact \`castle-story-archipelago-test-kit\` dans la
   dernière action GitHub verte et **extraire tout le ZIP**.
2. Fermer le jeu complètement.
3. Ouvrir PowerShell dans le dossier extrait du ZIP et exécuter :

\`\`\`powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Install-CastleStoryAP.ps1 -GameDirectory "C:\Program Files (x86)\Steam\steamapps\common\Castle Story"
\`\`\`

Adapter le chemin à l'installation réelle. Le script :

- vérifie le dossier du jeu et les trois fichiers requis ;
- arrête uniquement l'ancien bridge lancé depuis ce dossier ;
- sauvegarde la DLL précédente, le bridge précédent et les fichiers de session ;
- installe la DLL, le bridge EXE et le logo, avec vérification SHA-256 ;
- vide uniquement \`session.json\` et \`bridge_status.json\` périmés.

**Il ne touche pas à \`connection.json\`, \`checks.json\`, aux sauvegardes
Castle Story ni aux récompenses suivies.** Si le jeu est encore ouvert,
le script refuse de remplacer les fichiers.

Relancer le jeu et se reconnecter par le logo. La nouvelle interface doit
afficher le **bridge 0.6.5** et ne plus montrer une ancienne session connectée
sur la base d'un fichier statique.

### Problème indépendant : ArchiLAN et Hybrid

Un joueur avec \`game_mode: hybrid\` et
\`conquest_map_capacities: {}\` **ne peut toujours pas générer de campagne
Hybride**, car le serveur doit connaître le nombre de shards à capturer.
Le log \`Not enough verified Conquest shards: need 3, catalog has 0\`
signifie que **la génération a échoué avant qu'un serveur de cette seed
puisse démarrer**. Mettre à jour le plugin ou le bridge ne la corrigera pas.

Pour un test de bout en bout immédiatement générable, utiliser
\`CastleStoryPlayer.yaml\` du bundle, avec \`game_mode: invasion\` et
\`invasion_map_pool: []\`. Dans ArchiLAN, **enregistrer le YAML du slot**,
générer une nouvelle seed, démarrer ce nouveau serveur, et se connecter
avec le nom du slot de ce nouveau serveur. Ce choix Invasion est un test
temporaire, **pas une modification de l'objectif Hybride final**.



The tester verified Invasion wave events, a real Conquest shard capture,
and checks reaching ArchiLAN. **The new mission launcher, map validation
and native bonus-unit spawning have compiled, but have not yet been
validated in a real Castle Story campaign. Use disposable solo saves only.**

## Menu / connexion de la v0.5.1

Le plugin possède maintenant un **vrai bouton avec le logo officiel Archipelago**
dans le coin supérieur gauche, un léger agrandissement animé au survol,
et une fenêtre **centrée** et stylisée (couleurs bleu nuit, cartes et boutons
personnalisés). Le panneau de debug n'empiète plus sur cette icône : **F8**
active ou masque les informations techniques dans le coin inférieur droit.

Lorsque tu es connecté et que tu ouvres **Nouvelle partie**, le mod présente
un écran de **missions Archipelago uniquement**, avec les cartes et objectifs
imposés par la seed. La liste peut défiler et la sélection est conservée
dans \`mission_selection.json\`. L'écran de campagne recouvre le sélecteur
classique et empêche les démarrages de carte/mode incompatibles au niveau
du hook natif. Le lancement utilise ensuite le véritable menu du jeu.

L'interface de sélection s'affiche même si le catalogue de la seed est
incomplet : dans ce cas, elle précise **pourquoi aucune mission n'est
disponible**, et ne laisse pas lancer une partie libre pendant la session AP.
Une génération avec le nouvel APWorld et des capacités de shards vérifiées
est nécessaire pour les campagnes Conquête/Hybride dont le catalogue est vide.

**À valider dans le jeu :** les événements de fermeture du menu natif,
la découverte du bon \`Asset_Map\`, le choix du \`Asset_Gamemode\` et
l'utilisation du lancement automatique. La compilation CI valide les API
Unity de base, pas encore ce flux d'interface réel.

L'image \`archipelago-logo.png\` du paquet est récupérée depuis
[les ressources officielles d'Archipelago](https://github.com/ArchipelagoMW/Archipelago/blob/main/data/icon.png)
pendant la CI. Pour l'installer, copie **tout le dossier**
\`BepInEx/plugins\` du kit et garde le sous-dossier
\`castle_story_ap\`, sinon le logo et le pont réseau manqueront.

## Correctifs v0.5.2

- L'icône Archipelago n'a plus de cadre sombre : le logo est affiché directement
  sur fond transparent, avec agrandissement progressif au survol.
- Le bouton **Déconnexion** est accessible depuis la fenêtre de connexion
  **et** depuis l'écran des missions. Le bouton Retour revient correctement
  à la connexion quand on a seulement ouvert un aperçu de la campagne.
- La déconnexion depuis le jeu prévaut même si le bridge avait été démarré
  avec `--server` et `--slot`. Le bridge coupe la connexion et publie
  l'état de session `connected=false`.
- Une connexion ArchiLAN réussie ne garantit pas que le slot possède un
  `slot_data.campaign`. Le client affiche désormais le protocole et le statut
  (`ready`, `unready`, `missing`, `invalid`), au lieu d'un message générique.

### Pourquoi « Aucune campagne reçue » malgré « Connecté » ?

C'est possible lorsqu'un serveur ArchiLAN héberge encore une **seed générée
avec une ancienne version de l'APWorld**. Le protocole Archipelago accepte
alors la connexion (le slot existe), mais `slot_data` ne contient aucune
liste de missions. Ce n'est pas une panne du WebSocket.

Pour obtenir les missions automatiques, **importe le dernier
`castle_story.apworld` dans ArchiLAN puis génère une nouvelle seed**
avec les options `game_mode`, `invasion_goal`,
`conquest_shards` et, pour Conquête/Hybride, un catalogue de maps aux
capacités vérifiées. **Mettre seulement à jour la DLL n'ajoute pas une
campagne à un ancien fichier .archipelago généré.**

Si le problème persiste avec une nouvelle seed, les fichiers de diagnostic
`session.json` et `bridge_status.json` du dossier
`BepInEx/config/castle_story_archipelago` indiquent `campaign_status`
et `campaign_message`. Ne transmets pas `connection.json` : il contient
potentiellement ton mot de passe.

## v0.6.1 — Générer une partie Invasion sans catalogue de cartes

L'erreur ArchiLAN `No Invasion map IDs configured` était due à
`invasion_map_pool: []`. Elle est corrigée dans le **nouvel APWorld**.

Une seed **Invasion** peut désormais être générée avec les options par
défaut, sans nom de carte à renseigner. Le serveur émet une mission
`auto:invasion`, puis le client attend que Castle Story ait ouvert
**Nouvelle partie** et découvert les véritables IDs et modes des maps
installées. Il choisit **une carte compatible**, de manière déterministe
pour la seed et le slot, puis conserve ce choix dans
`BepInEx/config/castle_story_archipelago/auto_map_selection.json`.
Si le jeu n'a pas exposé de cartes compatibles, il signale un problème
au lieu de lancer une carte arbitraire. L'interface marque la carte
comme **en attente** jusqu'à la découverte native.

On peut toujours configurer `invasion_map_pool` pour que le **serveur**
choisisse lui-même parmi des IDs de carte connus. Sans catalogue, c'est
le **jeu** qui choisit, sous l'effet de la seed Archipelago.

**Hybride :** `game_mode: hybrid` exige toujours des capacités de shards
vérifiées dans `conquest_map_capacities`. L'Invasion peut être automatique,
pas la quantité de shards Conquête : le jeu ne peut pas deviner un objectif
multi-cartes correct.

Pour résoudre le message d'erreur rencontré sur ArchiLAN :

1. Télécharger le nouveau paquet de test.
2. Importer son `castle_story.apworld` dans ArchiLAN (remplacer l'ancien).
3. **Générer une nouvelle seed**. Avec le mode Invasion par défaut,
   aucun `invasion_map_pool` n'est nécessaire.
4. Installer également la nouvelle DLL et le pont réseau du paquet dans
   Castle Story.
5. Se connecter, ouvrir **Nouvelle partie** et laisser le jeu charger le
   catalogue natif. La mission Invasion se résout ensuite automatiquement.

La CI teste le calcul des maps et le transfert local, mais ne remplace
pas un test réel de `Asset_Map` et `GetGamemodes` sur cette installation.

## Campagnes avec cartes non localisées (v0.6.0)

Les noms affichés par Castle Story changent avec la langue :
`Pioneer Pass` peut être traduit. Ils ne doivent plus être comparés
au contenu de la seed. Le protocole v5 utilise les véritables **IDs de
dossiers d'assets**, préfixés par `official:` ou `workshop:`.

**Important :** une seed générée avec l'ancien APWorld ne peut pas être
réparée par la DLL seule. Il faut importer le `castle_story.apworld` du
nouveau paquet dans ArchiLAN, renseigner un pool de maps et générer
une **nouvelle** partie.

Le choix fixe de `Pioneer Pass` a été supprimé. Une campagne Invasion
ou Hybride requiert au moins une map Invasion explicitement connue ;
le générateur sélectionne une carte au hasard de manière **déterministe
pour la seed**. Fournir plusieurs IDs permet de varier d'une run à l'autre.
En Hybride, il exige aussi au moins une mission Conquête ; sinon la
génération échoue avec une explication, au lieu de fournir seulement la
mission Invasion.

### Inventorier automatiquement les cartes du jeu

1. Avec le mod installé, ouvrir **Nouvelle partie** au moins une fois.
   Le plugin écrit `BepInEx/config/castle_story_archipelago/installed_maps.json`
   depuis les assets réellement connus de Castle Story. Si le jeu expose
   `GetGamemodes`, la liste contient également les IDs compatibles Invasion
   et Conquête.
2. Dans le dossier du kit extrait, lancer le scanner avec Python :

```powershell
py .\inspect_maps.py "C:\Program Files (x86)\Steam\steamapps\common\Castle Story"
```

Il produit un `castle_story_map_options.yaml` contenant automatiquement
les IDs Invasion détectés, plus un rapport des cartes. **Ne pas inventer le
nombre de shards** : après confirmation en jeu ou via un format de marqueurs
fiable, relancer par exemple avec
`--approve "official:ID_VERIFIE=4"` (valeur fictive). Le scanner ajoute
alors les maps Conquête au YAML.

Importer ces options dans la configuration du slot Castle Story sur ArchiLAN,
puis générer une nouvelle seed. `installed_maps.json` est un fichier de
diagnostic non sensible qui peut être communiqué pour fiabiliser le catalogue.

Le scan de dossiers du client Python est désormais **indicatif** : une map
absente de `Info/Maps` peut être fournie comme asset empaqueté. Le menu
Unity vérifie la véritable identité native avant le lancement ; le bridge
ne refuse plus une campagne simplement parce qu'un dossier manque.

## Three campaign modes

A player slot chooses **invasion**, **conquest**, or **hybrid** in its YAML.
Hybrid requires both the configured Invasion wave goal and Conquest's
shard goal plus final victory. The planner chooses seed-specific map
combinations covering a configurable 1–30 Conquest shards, minimizing excess
shards and then the number of maps. It does not invent map counts.

Example slot:

```yaml
name: CastleStoryPlayer
game: Castle Story
Castle Story:
  game_mode: hybrid
  invasion_goal: 5
  conquest_shards: 7
  invasion_map_pool:
    - Helix
  conquest_map_capacities:
    "My Verified Map A": 3
    "My Verified Map B": 4
```

**The two `My Verified Map` names/counts above are format examples ONLY.**
Use your own maps and counts confirmed from the actual game. If the catalog
cannot cover the shard goal, the campaign is marked not ready. ArchiLAN must
generate a new seed with the v0.5.0 .apworld before a Hybrid run can be used.

## Installing the complete test kit

Download the latest *green* GitHub Actions artifact named
`castle-story-archipelago-test-kit`. Extract it and copy the **contents of
the `BepInEx/plugins` directory** into your existing
`Castle Story/BepInEx/plugins` folder, preserving its subdirectories.

The test kit includes:

- `CastleStoryArchipelago.dll`: Unity/BepInEx mod.
- `BepInEx/plugins/castle_story_ap/castle-story-ap-bridge.exe`: self-contained
  Windows bridge. Clicking **Connect** in the in-game AP popup starts it
  automatically; **no Python installation is required** for this path.
- `castle_story.apworld`: install/upload in ArchiLAN.
- `Start-Bridge.ps1` and Python sources for optional manual diagnostics.
- An example slot YAML.

In Castle Story, use the **Archipelago logo** at the upper left to open/close the
connection popup. Enter the full `wss://` ArchiLAN URL, the slot name and
optional password. A connection creates `connection.json` locally; it
contains the slot password in plaintext. **Do not upload that file or share
it with logs.**

While a ready AP campaign is connected, selecting a mission determines
the allowed map and mode. The plugin hooks the real Castle Story
`BaseMapStarterMenu.OnOk` to block accidental starts on other maps/modes.
It also observes the game's new-map library and offers **Launch selected
AP mission**. For this first integration test, open the game's native
**New Game** screen once so its map assets and starter context are loaded.
The plugin then tries to select the exact seeded map, choose its game
mode, and call the native start action. Native API interactions need a
real-game validation before the vanilla menu can safely be removed.

## Map catalog — no guessed shard counts

The map inspector reads the official `Info/Maps` and Workshop
`Info/UGC/Maps` folders, reads `LuaMarkersCount.json`, and reports
shard/crystal marker evidence without treating every crystal as a
capturable shard.

From a terminal with Python:

```powershell
py tools/inspect_maps.py "C:\Program Files (x86)\Steam\steamapps\common\Castle Story"
```

This creates `castle_story_map_inventory.json` and
`castle_story_map_options.yaml`. After validating specific maps and shard
counts in Castle Story, approve them:

```powershell
py tools/inspect_maps.py "C:\Program Files (x86)\Steam\steamapps\common\Castle Story" --approve "Actual Map=4" --approve "Another Actual Map=5"
```

The generated `conquest_map_capacities` YAML can be pasted into the slot
configuration in ArchiLAN. The Windows bridge also checks whether the
maps in a seed exist on the local machine, and marks the campaign unready
if verified locally missing.

## Equipped Bricktron reward delivery — experimental

`Equipped Worker`, `Equipped Archer`, `Equipped Knight` and
`Equipped Alchemist` have distinct item IDs. The mod persists an AP
item-indexed delivery journal per seed; duplicates are not automatically
regranted after a reconnect/restart.

An **experimental native SpawnUnit adapter** records a normal, locally
owned Bricktron spawn as a template. If explicitly enabled, it can replay
the game's spawn API with the requested occupation. It uses a two-phase
`spawning`→`delivered/review` journal so an interrupted call is never
blindly retried. Importantly, an equipped unit is not considered delivered
unless an actual, locally owned GameObject is returned.

The default config keeps `EnableNativeBonusSpawns = false`.
This adapter is **NOT proven to bypass Home Crystal quotas, preserve kits,
respawn, or survive reloading**. `GeneralRules.bricktronCap` is not raised.
A truly quota-independent bonus unit still requires game instrumentation and
validation of its ownership and Crystal count sources.

Do not enable experimental native spawns for real play. If you want to
test the adapter on a disposable save, do so knowing that extra units
could interact incorrectly with game population limits.

## Diagnostics

- `F8`: check/session status.
- `F9`: read-only menu, map, spawn and crystal API signature report.
- `F10`: bonus delivery queue, native template and occupation names.
- `BepInEx/LogOutput.log`: game diagnostics (review for credentials before
  sharing).
- `python -m unittest discover -s tests -v`: automated bridge/catalog tests.

Detailed architecture and limitations:
[docs/CAMPAIGN_ARCHITECTURE.md](docs/CAMPAIGN_ARCHITECTURE.md).

The proprietary Castle Story assemblies are **never** shipped.
