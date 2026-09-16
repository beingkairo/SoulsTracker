import { HostedClient, parseHostedLocation } from "./hosted-client.js";
import { renderHosted } from "./hosted-renderer.js";
const root = document.getElementById("souls-tracker-overlay");
if (root) {
  let client: HostedClient | undefined;
  const loadLocation = () => {
    client?.stop();
    client = undefined;
    root.replaceChildren();
    const location = parseHostedLocation(window.location.href);
    if (location) {
      client = new HostedClient(location, (death, appearance) => renderHosted(root, death, appearance, "__HOSTED_SKULL__"));
      client.start();
    }
  };
  loadLocation();
  window.addEventListener("hashchange", loadLocation);
  window.addEventListener("pagehide", () => client?.stop());
  window.addEventListener("pageshow", event => { if (event.persisted) client?.start(); });
}
