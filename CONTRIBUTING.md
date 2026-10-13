# Contribuer à UniFiles

Merci de ton intérêt ! Quelques règles simples pour garder le projet propre.

## Avant de commencer

- Ouvre une *issue* pour discuter d'une nouvelle fonctionnalité avant de coder.
- Toute nouvelle bibliothèque doit être **compatible avec la GPL v3**.
- UniFiles est 100 % local : aucune fonctionnalité ne doit envoyer de fichier ni de donnée sur Internet (hors vérification des mises à jour).

## En pratique

1. Installe le [SDK .NET 10](https://dotnet.microsoft.com/download).
2. Crée une branche depuis `main`.
3. Vérifie que `dotnet build` ne produit aucun avertissement et que `dotnet test` passe.
4. Ouvre une *pull request* qui explique le pourquoi du changement.

## Conventions

- Code, commentaires et messages de commit en anglais. Textes de l'interface en français et en anglais.
- **Aucun texte en dur dans l'interface** : ajoute chaque chaîne à `src/UniFiles.Core/Resources/Strings.resx` (anglais) **et** à `Strings.fr.resx` (français).
- **Aucune couleur, aucun rayon ni espacement en dur** : utilise les ressources de `src/UniFiles.App/Themes/Tokens.axaml`.
- Le citron (`#D7FF3D`) porte toujours un texte encre, jamais blanc. Les couleurs de module servent de repère, pas de fond de texte.
- Les fichiers d'origine ne sont **jamais** modifiés. Un outil écrit toujours via `OutputFolder.Reserve` (ou `FileToolBase.WriteOutputAsync`).
- Les messages d'erreur sont écrits pour l'utilisateur : ils disent ce qui s'est passé et quoi faire (`ToolException`).

## Ajouter un outil

1. Crée une classe qui hérite de `FileToolBase` dans `src/UniFiles.Core/Tools/<Module>/`.
2. Décris ses réglages dans `Options` (`ChoiceOption`, `NumberOption`, `BoolOption`, `TextOption`).
3. Enregistre-la dans `ToolCatalog`.
4. Ajoute ses textes aux deux fichiers `.resx` et un test dans `tests/UniFiles.Core.Tests`.
