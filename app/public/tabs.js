(function (scope) {
  function createTabController() {
    let activeTab = 'history', wasInMatch = false;
    return {
      get activeTab() { return activeTab; },
      choose(tab) { if (['history', 'match'].includes(tab)) activeTab = tab; },
      sync(inMatch) {
        const isInMatch = Boolean(inMatch);
        if (isInMatch && !wasInMatch) activeTab = 'match';
        if (!isInMatch && wasInMatch) activeTab = 'history';
        wasInMatch = isInMatch;
        return activeTab;
      }
    };
  }
  if (typeof module !== 'undefined' && module.exports) module.exports = { createTabController };
  else scope.createTabController = createTabController;
})(typeof window !== 'undefined' ? window : globalThis);
