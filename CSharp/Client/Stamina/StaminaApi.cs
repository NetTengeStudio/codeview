namespace ScpItdMod.Stamina {
    public static class StaminaApi {
        private static bool locked;
        private static bool winded;

        public static float Value => StaminaClient.Display;

        public static float Exhaustion => StaminaClient.Exhaustion;

        public static bool Exerting => StaminaClient.Exerting;

        public static bool Locked => locked;

        public static bool Winded => winded;

        public static void Reset() {
            locked = false;
            winded = false;
        }

        public static void Observe() {
            locked = StaminaClient.Locked;
            winded = StaminaSim.Winded(Value);
        }
    }
}
