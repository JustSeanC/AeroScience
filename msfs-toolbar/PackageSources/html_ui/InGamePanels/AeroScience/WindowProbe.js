// Lifecycle only: the installed simulator template owns every window handler.
class IngamePanelAeroScienceWindowProbe extends TemplateElement {
  connectedCallback() {
    super.connectedCallback();
  }
}
window.customElements.define("ingamepanel-aeroscience-window-probe", IngamePanelAeroScienceWindowProbe);
checkAutoload();
