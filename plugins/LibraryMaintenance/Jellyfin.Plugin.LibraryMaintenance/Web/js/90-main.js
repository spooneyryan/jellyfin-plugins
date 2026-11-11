// Start-up. The kit waits for window.ApiClient (polled every 25 ms) and for the signed-in user before it runs its
// scanners and its item-menu observer; 40-move-menu.js has added Move's entry, its viewshow check
// and its user listener already. Nothing here touches the page before the kit starts.

kit.start();
