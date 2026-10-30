namespace ScpItdMod.People {
    public static class ProfileUiPolicy {
        public static uint ResolveAfterBook(uint shown, uint active, uint created, bool shownExists,
            bool createSucceeded, bool createDraftExists, bool createdExists) {
            if (shown != 0 && !shownExists) { shown = active; }
            if (shown != 0 || createDraftExists) { return shown; }
            if (createSucceeded && created != 0 && createdExists) { return created; }
            return active;
        }
    }
}
