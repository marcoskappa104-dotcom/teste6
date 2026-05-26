using System;
using System.Collections.Generic;
using UnityEngine;

namespace RPG.Data
{

    [Serializable]
    public class CharacterData
    {
        public const int MAX_LEVEL              = 99;
        public const int MAX_ALLOCATED_PER_STAT = 300;
        public const int POINTS_PER_LEVEL_UP    = 5;

        public string        CharacterId;
        public string        CharacterName;
        public CharacterRace Race;

        public int RaceInt
        {
            get => (int)Race;
            set => Race = (CharacterRace)value;
        }

        private int _level = 1;
        public int Level
        {
            get => _level;
            set
            {
                int clamped = Math.Max(1, Math.Min(value, MAX_LEVEL));
                if (clamped != _level) _statsCacheDirty = true;
                _level = clamped;
            }
        }

        public long Experience            = 0;
        public long ExperienceToNextLevel = 100;

        public BaseAttributes    BaseAttributes   = new BaseAttributes();
        public EquipmentBonuses  EquipmentBonuses = new EquipmentBonuses();

        public float  PosX, PosY, PosZ;
        public string CurrentMap = "World_01";

        public float CurrentHP;
        public float CurrentMP;

        public int FreeAttributePoints = 0;

        public int AllocatedSTR, AllocatedAGI, AllocatedVIT;
        public int AllocatedDEX, AllocatedINT, AllocatedLUK;

        // ── Cache de DerivedStats ──────────────────────────────────────────
        [NonSerialized] private DerivedStats _cachedStats;
        [NonSerialized] private bool         _statsCacheDirty = true;

        public void InvalidateStatsCache() => _statsCacheDirty = true;

        /// <summary>
        /// Retorna stats derivados. Cache só é usado quando useCache=true e
        /// não há buff envolvido.
        /// </summary>
        public DerivedStats GetDerivedStats(BuffBonuses buff = null, bool useCache = false)
        {
            if (useCache && buff == null && !_statsCacheDirty && _cachedStats != null)
                return _cachedStats;

            var stats = StatsCalculator.Calculate(
                BaseAttributes,
                Level,
                Race,
                AllocatedSTR, AllocatedAGI, AllocatedVIT,
                AllocatedDEX, AllocatedINT, AllocatedLUK,
                EquipmentBonuses,
                buff);

            if (buff == null)
            {
                _cachedStats     = stats;
                _statsCacheDirty = false;
            }

            return stats;
        }

        public static long GetExperienceForLevel(int level)
        {
            int clamped = Math.Max(1, Math.Min(level, MAX_LEVEL));
            return (long)(100.0 * Math.Pow(clamped, 1.5));
        }

        /// <summary>
        /// Adiciona experiência e processa level-ups em cascata.
        /// Retorna true se houve ao menos um level up.
        /// </summary>
        public bool AddExperience(long amount)
        {
            if (amount <= 0) return false;
            if (Level >= MAX_LEVEL) return false;

            Experience += amount;
            bool leveled = false;

            while (Experience >= ExperienceToNextLevel && Level < MAX_LEVEL)
            {
                Experience          -= ExperienceToNextLevel;
                Level++;
                FreeAttributePoints += POINTS_PER_LEVEL_UP;

                ExperienceToNextLevel = Level >= MAX_LEVEL
                    ? 0L
                    : GetExperienceForLevel(Level);

                leveled = true;
            }

            if (Level >= MAX_LEVEL)
            {
                ExperienceToNextLevel = 0;
                if (Experience < 0) Experience = 0;
            }

            if (leveled) _statsCacheDirty = true;
            return leveled;
        }

        /// <summary>
        /// Remove experiência (penalidade de morte). Não reduz o level.
        /// </summary>
        public void RemoveExperience(long amount)
        {
            if (amount <= 0) return;
            Experience = Math.Max(0, Experience - amount);
        }

        /// <summary>
        /// Cria uma cópia profunda deste CharacterData.
        /// FIX: usa o setter de Level (não o campo privado _level) para garantir
        /// que o cache sujo seja marcado corretamente no clone.
        /// </summary>
        public CharacterData Clone()
        {
            var clone = new CharacterData
            {
                CharacterId           = CharacterId,
                CharacterName         = CharacterName,
                Race                  = Race,
                Experience            = Experience,
                ExperienceToNextLevel = ExperienceToNextLevel,
                PosX = PosX, PosY = PosY, PosZ = PosZ,
                CurrentMap            = CurrentMap,
                CurrentHP             = CurrentHP,
                CurrentMP             = CurrentMP,
                FreeAttributePoints   = FreeAttributePoints,
                AllocatedSTR = AllocatedSTR, AllocatedAGI = AllocatedAGI,
                AllocatedVIT = AllocatedVIT, AllocatedDEX = AllocatedDEX,
                AllocatedINT = AllocatedINT, AllocatedLUK = AllocatedLUK,
                BaseAttributes = new BaseAttributes
                {
                    STR = BaseAttributes.STR, AGI = BaseAttributes.AGI,
                    VIT = BaseAttributes.VIT, DEX = BaseAttributes.DEX,
                    INT = BaseAttributes.INT, LUK = BaseAttributes.LUK
                },
                EquipmentBonuses = new EquipmentBonuses
                {
                    STR             = EquipmentBonuses.STR,
                    AGI             = EquipmentBonuses.AGI,
                    VIT             = EquipmentBonuses.VIT,
                    DEX             = EquipmentBonuses.DEX,
                    INT             = EquipmentBonuses.INT,
                    LUK             = EquipmentBonuses.LUK,
                    ATK             = EquipmentBonuses.ATK,
                    DEF             = EquipmentBonuses.DEF,
                    MATK            = EquipmentBonuses.MATK,
                    MDEF            = EquipmentBonuses.MDEF,
                    HPBonus         = EquipmentBonuses.HPBonus,
                    MPBonus         = EquipmentBonuses.MPBonus,
                    ResistFire      = EquipmentBonuses.ResistFire,
                    ResistIce       = EquipmentBonuses.ResistIce,
                    ResistPoison    = EquipmentBonuses.ResistPoison,
                    ResistLightning = EquipmentBonuses.ResistLightning
                }
            };

            // FIX: usa o setter para passar pelo clamp e marcar cache como sujo.
            // Não acessa _level diretamente porque o campo é privado — o setter é o caminho correto.
            clone.Level = _level;

            // Clone sempre começa com cache sujo (EquipmentBonuses pode diferir).
            clone._statsCacheDirty = true;

            return clone;
        }
    }

    /// <summary>
    /// Container leve usado apenas em mensagens de rede.
    /// </summary>
    [Serializable]
    public class AccountData
    {
        public string              Username;
        public string              PasswordHash;
        public List<CharacterData> Characters = new List<CharacterData>();
        public string              LastLogin;
    }
}