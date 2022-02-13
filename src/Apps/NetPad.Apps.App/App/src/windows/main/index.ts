import Split from "split.js";
import {BuiltinShortcuts, IShortcutManager, Settings, Shortcut} from "@domain";
import {IPaneManager, PaneHost, PaneHostOrientation} from "@application";
import {ClipboardPane, NamespacesPane} from "./panes";
import {KeyCode} from "@common";

export class Index {
    public rightPaneHost: PaneHost;

    constructor(
        readonly settings: Settings,
        @IShortcutManager readonly shortcutManager: IShortcutManager,
        @IPaneManager readonly paneManager: IPaneManager) {
    }

    public async binding() {
        this.shortcutManager.initialize();
        this.registerKeyboardShortcuts();
    }

    public attached() {
        Split(["sidebar", "script-environments"], {
            gutterSize: 6,
            sizes: [14, 86],
            minSize: [100, 300],
        });

        const viewStateController = {
            split: null,
            expand: (paneHost) => {
                viewStateController.split = Split(["#content-left", `pane-host[data-id='${paneHost.id}']`], {
                    gutterSize: 6,
                    sizes: [85, 15],
                    minSize: [300, 100],
                });
            },
            collapse: (paneHost) => {
                viewStateController.split?.destroy();
            }
        };

        this.rightPaneHost = this.paneManager.createPaneHost(PaneHostOrientation.Right, viewStateController);
        this.paneManager.addPaneToHost(NamespacesPane, this.rightPaneHost);
        this.paneManager.addPaneToHost(ClipboardPane, this.rightPaneHost);
    }

    private registerKeyboardShortcuts() {
        for (const builtinShortcut of BuiltinShortcuts) {
            this.shortcutManager.registerShortcut(builtinShortcut);
        }

        this.shortcutManager.registerShortcut(
            new Shortcut("Namespaces Pane")
                .withAltKey()
                .withKey(KeyCode.KeyN)
                .hasAction(() => this.paneManager.activateOrCollapse(NamespacesPane))
                .configurable()
                .enabled()
        );
    }
}