import { Scene } from '@babylonjs/core/scene';
import { AdvancedDynamicTexture } from '@babylonjs/gui/2D/advancedDynamicTexture';
import { Button } from '@babylonjs/gui/2D/controls/button';
import { StackPanel } from '@babylonjs/gui/2D/controls/stackPanel';
import { Control } from '@babylonjs/gui/2D/controls/control';
import { postCommandToSim } from '../sceneRunner';

export interface CommandButton {
    label: string;
    /** Payload-free sim verb; posted as `/api/{gameKey}/{verb}`. */
    verb: string;
}

/**
 * Bottom-left demo control strip: each button posts one payload-free command through the
 * host bridge (`SimulationHost.SendCommand` on the C# side). Low-frequency, primitive-only —
 * exactly the sanctioned command path.
 */
export function buildCommandButtons(scene: Scene, gameKey: string, buttons: CommandButton[]): AdvancedDynamicTexture {
    const gui = AdvancedDynamicTexture.CreateFullscreenUI(`controls-${gameKey}`, true, scene);
    gui.idealWidth = 1920;

    const panel = new StackPanel(`buttons-${gameKey}`);
    panel.isVertical = false;
    panel.width = `${buttons.length * 150}px`;
    panel.height = '40px';
    panel.horizontalAlignment = Control.HORIZONTAL_ALIGNMENT_LEFT;
    panel.verticalAlignment = Control.VERTICAL_ALIGNMENT_BOTTOM;
    panel.left = '16px';
    panel.top = '-16px';

    for (const { label, verb } of buttons) {
        const button = Button.CreateSimpleButton(`btn-${verb}`, label);
        button.width = '140px';
        button.height = '40px';
        button.horizontalAlignment = Control.HORIZONTAL_ALIGNMENT_LEFT;
        button.verticalAlignment = Control.VERTICAL_ALIGNMENT_CENTER;
        button.color = '#e2e8f0';
        button.background = '#334155';
        button.cornerRadius = 8;
        button.fontSize = 15;
        button.paddingLeft = '10px';
        button.paddingRight = '10px';
        button.onPointerClickObservable.add(() => postCommandToSim(`/api/${gameKey}/${verb}`));
        panel.addControl(button);
    }

    gui.addControl(panel);
    return gui;
}
