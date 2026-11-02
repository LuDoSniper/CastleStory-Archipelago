# Castle Story Archipelago — Campagne v0.4.0 (travaux en cours)

## Contrat de campagne

Un slot Archipelago choisit `invasion`, `conquest` ou `hybrid`.

Le `.apworld` produit un `slot_data.campaign` pour le client; celui-ci le
synchronise dans `BepInEx/config/castle_story_archipelago/session.json`.
Un manifeste contient : `ready`, `warning`, `mode`, `missions[]`;
une mission indique `id`, `mode`, `map`, `wave_goal`,
`shard_start`, `shard_count`, `required`.

- Invasion : au moins une mission, avec objectif de vagues configurable (1–10).
- Conquête : la seed choisit un ensemble de cartes dont le total de shards
  vérifiés couvre le seuil de 1–30, en minimisant d'abord l'excédent puis le
  nombre de cartes. Le dernier candidat est arbitrés avec le seed.
- Hybride : contient les deux familles; le serveur ne reçoit le GOAL que
  si la vague cible, la victoire Conquête et le dernier check de shard sont vus.

Les identifiants préexistants des checks restent stables. Les récompenses
`Equipped Worker/Archer/Knight/Alchemist` ont des ID réservés.

## Ce qui est déjà codé

- Options YAML et plan de campagne déterministe avec tests.
- Filtrage des IDs par `slot_data.location_ids`, respect du mode Hybride et
  de ses conditions de fin dans le client.
- Connexion pilotée depuis l'overlay AP via un fichier local
  `connection.json`. Un client Python démarré sans `--server` se charge de
  la connexion; il sait détecter une demande de déconnexion ou de reconnexion.
- Liste des missions dans l'overlay Unity, avec sélection persistée localement
  via `mission_selection.json`. La sélection **n'entraîne pas encore le
  lancement automatique** d'une carte.
- F9 : inspection en lecture seule des signatures de menus, cartes,
  cristaux et créations d'unités dans le `LogOutput.log`.
- HUD d'inventaire reçu; aucun effet physique d'objet nouveau ne doit être
  considéré validé sur cette version.

## Bloqueurs techniques à résoudre avant de revendiquer une campagne jouable

1. **Catalogue réel des cartes** : les shards ne sont pas stockés dans le
   .apworld. Le script `tools/inspect_maps.py` collecte un inventaire
   non sensible et donne un rapport à valider. Aucune valeur de capacité
   n'est inventée. Un catalogue vide rend `campaign.ready=false`.
2. **Menu natif** : il faut identifier précisément le processus de création
   et de chargement de partie puis remplacer la sélection libre uniquement
   pendant une session AP. L'overlay actuel est une interface provisoire.
3. **Progression par carte / sauvegarde** : vérifier la carte réellement
   lancée, lier chaque partie à sa seed, aux objectifs propres à la carte,
   stocker les captures distinctes durablement et gérer les reloads.
4. **Bricktrons hors plafond du cristal** : valider `SpawnUnit`,
   `bricktronCap`, l'appartenance de faction, l'équipement et le respawn.
   Ne jamais contourner un compteur avant d'avoir déterminé s'il s'agit du
   quota du cristal, d'une limite de simulation ou des deux.
5. **Récompenses physiques** : traiter les objets une seule fois, après
   confirmation de l'action par le jeu; garder une file de livraison
   persistante en cas de crash, et empêcher le duplication au rechargement.
6. **Réconciliation réseau** : les checks confirmés par le serveur doivent
   être rechargés après reconnexion, pas uniquement déduits du fichier local.
7. **Vraie génération ArchiLAN** : une CI Python/C# verte ne remplace pas
   le test d'introspection/préflight de la dernière .apworld dans ArchiLAN.

## Connexion depuis le jeu (prototype)

1. Installer le DLL du bundle dans `BepInEx/plugins`.
2. Démarrer `Start-Bridge.ps1` **sans** `-Server` ni `-Slot`
   (mais avec `-GameDirectory` si installation non standard).
3. Lancer Castle Story, cliquer sur le bouton `AP` en haut à gauche,
   saisir l'URL WSS ArchiLAN, le nom du slot et éventuellement le mot de passe.
4. Cliquer sur Connect. L'overlay affiche le mode et les cartes de la seed.

Le bouton `AP` est **un substitut provisoire au logo officiel**, pas un
asset officiel. Le mot de passe est conservé localement dans
`BepInEx/config/castle_story_archipelago/connection.json` pour le pont Python;
ne pas partager ce fichier. Le jeu original reste accessible; sa sélection
de cartes n'est **pas encore bloquée**.
