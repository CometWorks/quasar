const ARMOR_FALLBACK_MODELS = {
    cube: model("Full armor cube.", [[-.5, -.5, -.5], [.5, -.5, -.5], [.5, .5, -.5], [-.5, .5, -.5], [-.5, -.5, .5], [.5, -.5, .5], [.5, .5, .5], [-.5, .5, .5]], [[0, 2, 1], [0, 3, 2], [4, 5, 6], [4, 6, 7], [0, 1, 5], [0, 5, 4], [3, 7, 6], [3, 6, 2], [1, 2, 6], [1, 6, 5], [0, 4, 7], [0, 7, 3]]),
    half_cube: model("Half-depth armor cube.", [[-.5, -.5, -.5], [.5, -.5, -.5], [.5, .5, -.5], [-.5, .5, -.5], [-.5, -.5, 0], [.5, -.5, 0], [.5, .5, 0], [-.5, .5, 0]], [[0, 2, 1], [0, 3, 2], [4, 5, 6], [4, 6, 7], [0, 1, 5], [0, 5, 4], [3, 7, 6], [3, 6, 2], [1, 2, 6], [1, 6, 5], [0, 4, 7], [0, 7, 3]]),
    wedge: mirrorZModel("Triangular-prism armor slope.", [[-.5, -.5, -.5], [.5, -.5, -.5], [-.5, -.5, .5], [.5, -.5, .5], [-.5, .5, .5], [.5, .5, .5]], [[0, 1, 3], [0, 3, 2], [2, 3, 5], [2, 5, 4], [0, 2, 4], [0, 4, 1], [1, 4, 5], [1, 5, 3]]),
    half_wedge: mirrorZModel("Low triangular-prism armor slope.", [[-.5, -.5, -.5], [.5, -.5, -.5], [-.5, -.5, .5], [.5, -.5, .5], [-.5, 0, .5], [.5, 0, .5]], [[0, 1, 3], [0, 3, 2], [2, 3, 5], [2, 5, 4], [0, 2, 4], [0, 4, 1], [1, 4, 5], [1, 5, 3]]),
    slope2_base: model("2x1 armor slope base half.", [[-.5, -.5, -.5], [.5, -.5, -.5], [-.5, -.5, .5], [.5, -.5, .5], [-.5, .5, -.5], [.5, .5, -.5], [-.5, 0, .5], [.5, 0, .5]], [[0, 4, 5], [0, 5, 1], [2, 3, 7], [2, 7, 6], [0, 1, 3], [0, 3, 2], [4, 6, 7], [4, 7, 5], [0, 2, 6], [0, 6, 4], [1, 5, 7], [1, 7, 3]]),
    slope2_tip: model("2x1 armor slope tip half.", [[-.5, -.5, -.5], [.5, -.5, -.5], [-.5, -.5, .5], [.5, -.5, .5], [-.5, 0, -.5], [.5, 0, -.5]], [[0, 4, 5], [0, 5, 1], [0, 1, 3], [0, 3, 2], [4, 2, 3], [4, 3, 5], [0, 2, 4], [1, 5, 3]]),
    corner2_base: model("2x1 armor corner base half.", [[-.5, -.5, -.5], [.5, -.5, -.5], [-.5, -.5, .5], [.5, -.5, .5], [-.5, .5, -.5], [.5, .5, -.5], [.5, 0, .5]], [[0, 4, 5], [0, 5, 1], [0, 1, 3], [0, 3, 2], [2, 3, 6], [0, 2, 4], [1, 5, 6], [1, 6, 3], [4, 2, 6], [4, 6, 5]]),
    corner2_tip: model("2x1 armor corner tip half.", [[0, -.5, -.5], [.5, -.5, -.5], [.5, 0, -.5], [.5, -.5, .5]], [[0, 2, 1], [0, 1, 3], [1, 2, 3], [0, 3, 2]]),
    tetra_corner: mirrorXModel("Triangular armor corner.", [[-.5, -.5, -.5], [.5, -.5, -.5], [-.5, -.5, .5], [-.5, .5, -.5]], [[0, 1, 2], [0, 3, 1], [0, 2, 3], [1, 3, 2]]),
    sloped_corner: model("Square-base sloped armor corner.", [[-.5, -.5, -.5], [.5, -.5, -.5], [.5, -.5, .5], [-.5, -.5, .5], [-.5, .5, -.5]], [[0, 1, 2], [0, 2, 3], [0, 4, 1], [0, 3, 4], [1, 4, 2], [2, 4, 3]]),
    cut_corner: model("Armor cube with local Right/Bottom/Front corner cut away.", [[-.5, -.5, -.5], [-.5, .5, -.5], [.5, .5, -.5], [-.5, -.5, .5], [.5, -.5, .5], [-.5, .5, .5], [.5, .5, .5]], [[0, 3, 5], [0, 5, 1], [2, 6, 4], [0, 4, 3], [1, 5, 6], [1, 6, 2], [0, 1, 2], [3, 4, 6], [3, 6, 5], [0, 2, 4]]),
    thin_side_panel: boxModel("Thin armor panel on one block side.", .4, .5, -.5, .5),
    thin_center_panel: boxModel("Thin centered armor panel.", -.05, .05, -.5, .5),
    thin_half_panel: boxModel("Thin side armor panel occupying half block height.", .4, .5, -.5, 0),
    thin_half_center_panel: boxModel("Thin centered armor panel occupying half block height.", -.05, .05, -.5, 0),
    thin_quarter_panel: boxModel("Thin side armor panel occupying quarter block height.", .4, .5, -.5, -.25),
    thin_wedge: model("Thin sloped armor panel.", [[.4, -.5, -.5], [.5, -.5, -.5], [.4, -.5, .5], [.5, -.5, .5], [.4, .5, .5], [.5, .5, .5]], [[0, 1, 3], [0, 3, 2], [2, 3, 5], [2, 5, 4], [0, 2, 4], [0, 4, 1], [1, 4, 5], [1, 5, 3]]),
    thin_half_wedge: model("Thin half-height sloped armor panel.", [[.4, -.5, -.5], [.5, -.5, -.5], [.4, -.5, .5], [.5, -.5, .5], [.4, 0, .5], [.5, 0, .5]], [[0, 1, 3], [0, 3, 2], [2, 3, 5], [2, 5, 4], [0, 2, 4], [0, 4, 1], [1, 4, 5], [1, 5, 3]]),
    thin_side_slope: model("Thin triangular side armor panel.", [[.4, -.5, -.5], [.5, -.5, -.5], [.4, -.5, .5], [.5, -.5, .5], [.4, .5, -.5], [.5, .5, -.5]], [[0, 1, 3], [0, 3, 2], [0, 4, 1], [1, 4, 5], [1, 5, 3], [0, 2, 4], [2, 3, 5], [2, 5, 4]]),
    thin_corner_panel: model("Thin corner armor panel.", [[.4, -.5, -.5], [.5, -.5, -.5], [.4, -.5, .5], [.5, -.5, .5], [.4, .5, -.5], [.5, .5, -.5]], [[0, 1, 3], [0, 3, 2], [0, 4, 1], [1, 4, 5], [1, 5, 3], [0, 2, 4], [2, 3, 5], [2, 5, 4]]),
};

export function armorFallbackModelForDefinition(definition) {
    const subtype = definitionSubtypeId(definition);
    if (!subtype || !isArmorSubtype(subtype)) return null;
    const lower = subtype.toLowerCase();
    return ARMOR_FALLBACK_MODELS[armorFallbackModelKey(lower)] || null;
}

export function armorFallbackModelCount() {
    return Object.keys(ARMOR_FALLBACK_MODELS).length;
}

function armorFallbackModelKey(subtype) {
    if (subtype.includes("panel")) return panelFallbackModelKey(subtype);
    if (subtype.includes("inv")) return "cut_corner";
    if (subtype.includes("slope2base")) return "slope2_base";
    if (subtype.includes("slope2tip")) return "slope2_tip";
    if (subtype.includes("corner2base")) return "corner2_base";
    if (subtype.includes("corner2tip")) return "corner2_tip";
    if (subtype.includes("cornersquare") || subtype.includes("raisedslopedcorner") || subtype.includes("slopedcornerbase") || subtype.includes("halfslopecorner") || subtype.includes("halfslopedcorner") || subtype.includes("squareslopedcornerbase")) return "sloped_corner";
    if (subtype.includes("halfslopearmorblock") || subtype.includes("halfslope") || subtype.includes("halfsloped")) return "half_wedge";
    if (subtype.includes("halfarmorblock")) return "half_cube";
    if (subtype.includes("halfcorn")) return "cube";
    if (subtype.includes("corner") || subtype.includes("slopedcornertip") || subtype.includes("squareslopedcornertip")) return "tetra_corner";
    if (subtype.includes("slope")) return "wedge";
    return "cube";
}

function panelFallbackModelKey(subtype) {
    if (subtype.includes("slopedside") || subtype.includes("slopeside")) return "thin_side_slope";
    if (subtype.includes("halfsloped")) return "thin_half_wedge";
    if (subtype.includes("sloped") || subtype.includes("slope")) return "thin_wedge";
    if (subtype.includes("quarter")) return "thin_quarter_panel";
    if (subtype.includes("halfcenter")) return "thin_half_center_panel";
    if (subtype.includes("half")) return "thin_half_panel";
    if (subtype.includes("center") || subtype.includes("face")) return "thin_center_panel";
    if (subtype.includes("corner")) return "thin_corner_panel";
    return "thin_side_panel";
}

function isArmorSubtype(subtype) {
    const lower = subtype.toLowerCase();
    return lower.includes("armor") && !lower.includes("armory");
}

function definitionSubtypeId(definition) {
    const id = String(definition && definition.id || "");
    if (!id) return "";
    const slash = id.lastIndexOf("/");
    return slash >= 0 ? id.slice(slash + 1) : id;
}

function model(description, vertices, triangles) {
    return { description, vertices, triangles, isBoxPlaceholder: description === "Full armor cube." };
}

function mirrorXModel(description, vertices, triangles) {
    return mirroredModel(description, vertices, triangles, ([x, y, z]) => [-x, y, z]);
}

function mirrorZModel(description, vertices, triangles) {
    return mirroredModel(description, vertices, triangles, ([x, y, z]) => [x, y, -z]);
}

function mirroredModel(description, vertices, triangles, transform) {
    return model(description, vertices.map(transform), triangles.map(([a, b, c]) => [a, c, b]));
}

function boxModel(description, x0, x1, y0, y1) {
    return model(description, [[x0, y0, -.5], [x1, y0, -.5], [x1, y1, -.5], [x0, y1, -.5], [x0, y0, .5], [x1, y0, .5], [x1, y1, .5], [x0, y1, .5]], [[0, 2, 1], [0, 3, 2], [4, 5, 6], [4, 6, 7], [0, 1, 5], [0, 5, 4], [3, 7, 6], [3, 6, 2], [1, 2, 6], [1, 6, 5], [0, 4, 7], [0, 7, 3]]);
}
