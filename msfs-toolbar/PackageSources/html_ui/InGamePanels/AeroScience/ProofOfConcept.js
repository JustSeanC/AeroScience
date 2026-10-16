class AeroScienceProbe extends TemplateElement {
  connectedCallback() {
    super.connectedCallback();
    Include.addScript("/JS/Services/CommBus.js", () => {
      this.listener = RegisterCommBusListener();
      this.listener.on("AeroScience.Reply.v1", (data) => {
        this.querySelector("#as-probe").textContent = data;
      });
      this.timer = setInterval(() => {
        if (
          !this.querySelector("#aeroscience-frame").classList.contains(
            "panelInvisible",
          )
        ) {
          const id = "xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx".replace(
            /[xy]/g,
            (c) => {
              const r = (Math.random() * 16) | 0;
              return (c === "x" ? r : (r & 3) | 8).toString(16);
            },
          );
          this.listener.callSimConnect(
            "AeroScience.Command.v1",
            JSON.stringify({ protocol: 1, requestId: id, op: "state" }),
          );
        }
      }, 1000);
    });
  }
  disconnectedCallback() {
    clearInterval(this.timer);
    if (this.listener) this.listener.unregister();
  }
}
window.customElements.define("ingamepanel-aeroscience-poc", AeroScienceProbe);
checkAutoload();
