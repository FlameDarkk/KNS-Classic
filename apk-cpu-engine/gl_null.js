// GL stand-in for headless runs: counts the calls.
function makeNullGL(counts) {
  const ret = { glGenTextures: 0, glIsEnabled: 0, glGetError: 0, glIsTexture: 1 };
  return new Proxy({}, {
    get(_, name) {
      if (typeof name !== 'string') return undefined;
      return () => {
        counts[name] = (counts[name] || 0) + 1;
        return ret[name] || 0;
      };
    },
  });
}
if (typeof module !== 'undefined') module.exports = { makeNullGL };
