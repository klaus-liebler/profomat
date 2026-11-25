// Eintrag für das three.js-Bündel des ProfOMat (3D-Vorschau der Gefache, wwwroot/gefach.js).
// Nur die benötigten Teile, damit das Bündel klein bleibt und ohne Import-Map (CSP) auskommt. Neu bauen: siehe README.md daneben.
export {
    AmbientLight, Box3, BufferGeometry, Color, DirectionalLight, DoubleSide, EdgesGeometry, ExtrudeGeometry, Float32BufferAttribute, Group,
    HemisphereLight, LineBasicMaterial, LineSegments, Mesh, MeshStandardMaterial, PerspectiveCamera, Scene, Shape, SRGBColorSpace, Vector3,
    WebGLRenderer,
} from 'three';
export { OrbitControls } from 'three/examples/jsm/controls/OrbitControls.js';
