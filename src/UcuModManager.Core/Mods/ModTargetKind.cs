namespace UcuModManager.Core.Mods;

public enum ModTargetKind
{
    BepInExPlugin,
    BepInExConfig,
    BepInExProfileConfig,
    BepInExPatcher,
    BepInExTranslation,
    BepInExOther,
    GameRootContent,
    GameDataContent,
    Documentation,
    Unknown
}
