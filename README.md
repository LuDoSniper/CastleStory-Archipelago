# Castle Story × Archipelago — v0.7.2 (menus pilotés par événements, diagnostics natifs)

Castle Story (Unity 5.6 / BepInEx 5) ↔ Archipelago ↔ ArchiLAN.

## v0.7.2 — Correctifs ciblés après un essai réel décevant

Le test de la v0.7.1 a révélé que **seule la duplication des cartes
avait disparu**. Le lag, l'absence du bouton Archipelago dans le menu
initial, l'absence du crédit et la présence des options non désirées
subsistaient. Les tests CI C# ne valident **pas** le rendu runtime Unity.

**Cause concrète du lag dans le code :** la précédente implémentation
parcourait tous les `UnityEngine.UI.Button` actifs et le texte de leurs
descendants toutes les 500 ms, même sans changement de menu.

Changements de cette passe :

- Le scan permanent est supprimé. Les méthodes `OpenSelf` des menus
  `Brix.UI.Builder.Menu.BaseMenu` sont interceptées ; après chaque
  ouverture, quelques passes courtes et limitées découvrent les cartes
  ajoutées par le moteur, puis le filtrage cesse complètement jusqu'au
  prochain changement d'écran. La nouvelle métrique `[AP UI PERF]`
  signale les passes dépassant 30 ms.
- Le menu Jouer est détecté à partir des ancêtres communs des boutons
  Nouvelle partie / Charger / Multijoueur, même si leurs parents Unity
  immédiats diffèrent. Le filtrage inclut maintenant les contrôles
  `Selectable` tels que les `Toggle`, et non seulement les `Button`.
- L'insertion du bouton Archipelago ne dépend plus strictement du fait
  que Jouer et Options soient frères directs. Un libellé traduit ou
  un nom de prefab `Button_Play` peut également servir de repère.
- Le crédit Archipelago est déclenché indépendamment du résultat de
  l'insertion du bouton, lorsque l'ouverture du menu titre est reconnue
  dans Brix ou par le contrôleur de menu principal. Il s'agit d'une
  séquence séparée après le chargement des menus, **pas d'une
  modification prouvée de la chronologie originale des studios**.
- Les classes de menus ouverts sont désormais consignées via
  `[AP NATIVE SCREEN]`, sans devoir appuyer manuellement sur F9.
  Si l'entrée native reste introuvable, le mod capture automatiquement
  une fois les boutons détectés via `[AP NATIVE UI PROBE]`.

**Limite importante :** sans le code de l'interface et des crédits du
jeu, nous ne pouvons pas démontrer que tous les menus natifs seront
masqués ni que le crédit démarrera à l'instant exact voulu. Cette passe
corrige les problèmes identifiables statiquement et fournit des
informations de runtime pour terminer l'intégration sans continuer
à deviner les classes du moteur.

**Test conseillé :** installer le kit 0.7.2, observer crédit / bouton
d'accueil, aller dans Jouer, Nouvelle partie puis Invasion, rester
sur les cartes pour vérifier l'absence de lag, faire Retour puis
Invasion. Si des menus sont encore visibles ou manquants, transmettre
le nouveau `BepInEx/LogOutput.log`. Les nouvelles traces enregistrent
automatiquement les classes de menu et la durée des filtrages.
La seed APWorld et les checks existants sont conservés.

## v0.7.1 — Menu natif, splash autonome et identifiants privés

Cette version suit le parcours vérifié par le joueur :
1. **Accueil** : Jouer, Archipelago (bouton cloné de Jouer en style primaire), Options.
   Il faut détecter Jouer ET Options dans la même branche du menu, pour
   éviter de cloner un bouton d'un sous-menu.
2. **Jouer** : Nouvelle partie, Charger, Multijoueur et Retour. Les autres
   entrées d'un groupe de menu reconnu sont masquées lorsque la seed est active.
3. **Nouvelle partie** : pas de Construction libre ; seulement Invasion,
   Conquête, ou les deux pour Hybride.
4. **Mode** : le catalogue des cartes visibles est limité aux cartes autorisées
   par la seed et le mode actuellement choisi ; les identifiants de cartes sont
   validés également dans le hook natif de lancement.

**Stabilité du filtrage :** les cartes masquées ne sont plus réactivées toutes
les 500 ms (ce qui déclenchait leurs OnEnable et pouvait dupliquer les tuiles).
Le filtrage est appliqué à l'ouverture native du menu et incrémental pour les
nouvelles cartes. Les cartes déjà masquées restent masquées lors de Retour, puis
réouverture du mode. Pour Hybride, elles sont recalculées seulement lors d'un
véritable changement de mode.

**Connexion :** le panneau principal utilise désormais un Canvas Unity UI,
les boutons natifs clonés de Jouer/Options et de vrais InputField Unity.
Le raccourci F7 ouvre ce même panneau ; l'ancien IMGUI n'apparaît qu'en
dernier recours si des types Unity UI sont absents. Le panneau propose
Connexion, Déconnexion et Fermer.

- `connection_preferences.json` conserve **uniquement** l'adresse et le nom
  du slot, réutilisés au lancement suivant.
- Le mot de passe n'est **pas** conservé sur disque par le nouveau flux :
  il est envoyé du jeu à l'exécutable bridge par un tuyau stdin local en
  mémoire, puis effacé des champs. Il doit être ressaisi à la prochaine
  connexion. L'installateur migre un éventuel ancien `connection.json`
  contenant un mot de passe en clair, garde seulement adresse/slot et
  supprime le fichier. Les backups/disques historiques ne peuvent pas être
  nettoyés à distance.
- Checks, progression, seed et cartes installées ne sont pas supprimés par
  la mise à jour.

**Crédits de démarrage :** une séquence blanche autonome de 3,5 secondes
apparaît **après** l'apparition de l'écran Jouer / Options, puis disparaît
avant de donner le contrôle. Le logo Archipelago est centré, le nom est noir
pour rester lisible sur fond blanc, l'ensemble apparaît/disparaît en fondu.
Cette réalisation n'édite pas les séquences propriétaires de Sauropod.
Une imitation exacte de leur chronologie interne nécessiterait leurs
objets/scripts de crédits, non fournis dans le dépôt.

**Points à valider dans le vrai jeu :** disposition exacte des boutons,
détection des vrais libellés de certains sous-menus, présentation du Canvas
de connexion et instant où le menu devient visible après les logos. La CI
compile le code et vérifie les contrats, mais ne peut pas faire tourner
le moteur Unity 5.6 et ses menus. F9 affiche les libellés des boutons
(`[AP NATIVE UI PROBE]`) utiles à l'adaptation.

**Test :** fermer Castle Story, installer le kit 0.7.1, relancer ;
observer le crédit dédié après les logos, puis le bouton Archipelago
entre Jouer et Options. Se connecter et vérifier que seul le couple
serveur/slot se remplit à la reconnexion (mot de passe vide).
Parcourir Jouer / Nouvelle partie / Invasion / Retour / Invasion,
surveiller l'absence de doublons et de saccades, puis vérifier qu'un
check continue à remonter à ArchiLAN.

## v0.7.0 — Navigation Archipelago dans les menus natifs (pré-validation)

Retour utilisateur confirmé : une partie Invasion s'est lancée,
les cinq checks sont parvenus à ArchiLAN et la run s'est achevée.
Le parcours passe donc de la popup de sélection à la navigation du jeu.

- Plus de logo flottant en haut à gauche. Le mod cherche le bouton
  **Jouer / Play** du véritable menu Unity et le clone sous celui-ci
  pour ajouter un bouton **Archipelago** reprenant son apparence et
  ses interactions natives, sans réutiliser l'action « Jouer ».
  **F7** ouvre le panneau de connexion si le bouton natif ne peut pas
  être identifié sur une version/langue du jeu.
- Le bouton Archipelago sert à se connecter/se déconnecter. La
  sélection des modes et cartes se fait maintenant directement via
  **Jouer > Nouvelle partie**, sans la popup des missions.
- Quand une seed valide est connectée, le mod masque les contrôles
  natifs dont le libellé est reconnu comme Construction libre, un
  mode interdit (Invasion ou Conquête), ou une carte absente de la
  seed. En Hybride, Invasion et Conquête restent proposées.
- Les identifiants techniques des cartes sont associés aux libellés
  du jeu via `GetAssetLabel`. Une carte native ne peut être lancée
  que si son ID **ET** son mode correspondent exactement à une
  mission de la seed : le contrôle `OnOk` reste actif même si un
  contrôle visuel n'a pas pu être masqué.
- L'action de démarrer dans le menu du jeu associe désormais
  automatiquement la mission correspondante et conserve la
  protection contre les faux checks d'une partie libre.
- Ajout d'un crédit discret **Archipelago** avec son logo officiel
  pendant les premières secondes du chargement, sans modifier ni
  remplacer les logos ou cinématiques propriétaires de Sauropod.
  Son instant d'apparition dépend de la séquence d'introduction du jeu.
- **F9** liste les contrôles et libellés natifs réellement trouvés
  (`[AP NATIVE UI PROBE]`) ; `[AP NATIVE UI]` signale
  l'insertion du bouton et le filtrage appliqué.

**Limite à confirmer en jeu :** la hiérarchie du menu natif et les
libellés de ses boutons ne sont pas disponibles dans les sources du mod.
Le filtrage est conservateur, uniquement sur les composants et
libellés reconnus. La fenêtre de **connexion** conserve temporairement
son style propre ; l'harmonisation pixel-perfect avec le panneau de
connexion natif reste à valider avec une capture du menu réel.
Ce kit ne prétend pas que tous les boutons ou cartes sont déjà
visuellement masqués sur toutes les versions de Castle Story.

**Test :** installer le kit v0.7.0 ; vérifier le nouveau bouton
Archipelago sous Jouer, se connecter, puis examiner Nouvelle partie :
Construction libre doit disparaître, seul le mode de la seed (ou les
deux modes Hybride) doit rester, puis seulement ses cartes.
Lancer une mission native, jouer une vague et vérifier qu'un check
arrive à ArchiLAN. Si un menu reste visible, faire F9 sur cet écran
et envoyer `BepInEx/LogOutput.log` ; cela permet d'adapter précisément
le filtre à la structure observée sans modifier la seed.

## v0.6.9 — Vérification des checks Invasion après cinq vagues

Les logs d'un essai réel confirment : lancement validé de la mission
`invasion_1` sur `official:Zuma`, et événements `InvasionWave` de 0 à 5.
Pourtant, F8 affichait `checks=0` et `connected=False`. **Cause de
l'absence de checks identifiée dans le code :** `EnableWaveChecks`
était `false` par défaut ; les coupures intermittentes de connexion
constituaient une seconde condition de blocage.

**Correctifs :**

- Après validation de la carte et du mode par le menu natif, les checks
  Invasion de la campagne Archipelago sont actifs **automatiquement**,
  même si un ancien `EnableWaveChecks=false` est conservé dans la config.
  Le flag historique reste réservé aux seeds sans manifeste.
- Les callbacks répétés de la même vague ne créent pas de doublons.
- Les checks de la mission effectivement lancée sont stockés dans
  `checks.json` avec le token de la seed, **même pendant une brève perte
  de connexion** ; le bridge les transmet à ArchiLAN lors du retour.
  L'écriture Windows utilise un remplacement atomique, avec retry en cas
  d'échec temporaire.
- Le mod ignore les vagues des parties libres et des missions
  simplement sélectionnées (mais pas réellement lancées).
- `[AP MISSION START]`, `[AP CHECK]`, `[AP CHECK FILE]` et F8
  rendent l'enregistrement et la transmission vérifiables.

La panne réseau intermittente observée ne peut pas être attribuée à une
cause précise à partir des seuls logs BepInEx. Cette version neutralise
son effet sur l'enregistrement des checks sans prétendre résoudre
l'origine du réseau.

**Test conseillé :** installer le kit v0.6.9, garder la même seed
ArchiLAN, démarrer une *nouvelle* partie Invasion sur la mission choisie,
et progresser sur les vagues. F8 doit montrer `activeMission=invasion_1`,
`autoChecks=True` et un nombre de checks non nul. Les checks d'une
ancienne partie v0.6.8 qui n'ont jamais été enregistrés dans
`checks.json` ne peuvent pas être reconstitués automatiquement depuis
le serveur.

## v0.6.8 — La mission apparaît, mais l'interface masque les erreurs

Un test réel a confirmé que le bridge 0.6.7 et Unity décodent correctement
une mission Invasion (`[AP MANIFEST] Transfert texte V1 : 1 mission(s)`).
Le journal ne contient aucune exception de popup, mais plusieurs transitions
du bouclier natif. Le client remplaçait aussi les messages de lancement par
l'état du bridge chaque seconde, ce qui rendait les erreurs illisibles.

- Les messages de sélection/lancement ont maintenant une zone dédiée et
  restent visibles jusqu'à une nouvelle action.
- Une coupure momentanée du heartbeat n'efface pas la popup ; elle affiche
  une indication de connexion et conserve le bouclier anti-clics.
- Le lancement sans `Nouvelle partie` ouverte indique précisément les
  étapes à suivre et produit `[AP LAUNCH]` dans les logs.
- Les exceptions IMGUI sont enregistrées en `[AP UI EXCEPTION]`, avec
  restauration de l'état GUI et équilibrage du scroll view.
- Les transitions de connexion sont enregistrées en `[AP SESSION]`.

**Test recommandé :** installe le kit v0.6.8, reconnecte-toi à la même seed
Invasion, ferme la fenêtre Archipelago, ouvre **Nouvelle partie** dans Castle
Story, sélectionne la mission et clique sur **Lancer cette mission**. Si le
lancement échoue, copie `[AP ACTION]`, `[AP LAUNCH]`, `[AP SESSION]` et
`[AP UI EXCEPTION]` du log : les erreurs restent affichées dans la fenêtre,
sans nécessiter de capture au bon moment.

La liste des missions est confirmée sur Unity 5.6, mais le lancement d'une
partie en jeu doit encore être validé manuellement.

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
