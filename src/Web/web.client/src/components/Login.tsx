
import './NavMenu.css';
import React from 'react';
import { SignInButton } from "./SignInButton";
import { AuthenticatedTemplate, UnauthenticatedTemplate } from "@azure/msal-react";

export function Login() {

    return (
        <div>
            <h1>Cold Storage Access Web</h1>
            <p>This application is for finding files moved into Azure Blob cold storage.</p>

            <AuthenticatedTemplate>
                <span>You are signed in. This page shouldn't load when signed-in, so this is awkward...</span>

            </AuthenticatedTemplate>
            <UnauthenticatedTemplate>
                <p>You are not signed in! Please sign in so we can get access to the data we need.</p>
                <SignInButton />
            </UnauthenticatedTemplate>
        </div>
    );
}
